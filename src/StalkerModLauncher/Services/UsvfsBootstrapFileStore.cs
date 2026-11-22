using System.Text.Json;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class UsvfsBootstrapFileStore
{
    private const string ManifestName = ".bootstrap-files.json";
    private static readonly EnumerationOptions RegularFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static void Record(string bootstrap, bool hasSeparateWorkingTree)
    {
        if (!Directory.Exists(bootstrap)) return;
        FileSystemSafety.EnsureNoDirectoryLinks(bootstrap);
        var files = Enumerate(bootstrap).Select(file => Path.GetRelativePath(bootstrap, file))
            .ToDictionary(path => path, _ => true, StringComparer.OrdinalIgnoreCase);
        files[string.Empty] = hasSeparateWorkingTree;
        ProfileWritableGameFileStore.WriteBaselineFile(bootstrap, ManifestName, files);
    }

    public static void Capture(string bootstrap, string profileWorkspace, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(bootstrap)) return;
        FileSystemSafety.EnsureNoDirectoryLinks(bootstrap);
        var manifest = Path.Combine(bootstrap, ManifestName);
        if (!File.Exists(manifest)) return;
        Dictionary<string, bool> baseline;
        try
        {
            baseline = new(JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException(Strings.Writable_BaselineInvalid), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex) { throw new InvalidDataException(Strings.Writable_BaselineInvalid, ex); }

        var overwrite = Path.Combine(profileWorkspace, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);
        var separateWorkingTree = baseline.GetValueOrDefault(string.Empty);
        var updated = false;
        foreach (var file in Enumerate(bootstrap))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(bootstrap, file);
            if (baseline.ContainsKey(relative)) continue;
            var targetRelative = relative;
            if (separateWorkingTree && relative.StartsWith(@"working-tree\", StringComparison.OrdinalIgnoreCase))
                targetRelative = relative[@"working-tree\".Length..];
            else if (separateWorkingTree && relative.StartsWith(@"images\", StringComparison.OrdinalIgnoreCase))
                targetRelative = relative[@"images\".Length..];
            var target = FileSystemSafety.ResolvePathInside(overwrite, targetRelative, Strings.Safety_ProfileWritableFile);
            FileSystemSafety.EnsureNoDirectoryLinks(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                ProfileWritableGameFileStore.CopyThroughTemporaryFile(file, target);
            else if (ProfileWritableGameFileStore.Hash(target) != ProfileWritableGameFileStore.Hash(file))
            {
                var conflict = Path.Combine(profileWorkspace, "diagnostics", $"{Path.GetFileName(file)}.conflict-{Guid.NewGuid():N}.bak");
                ProfileWritableGameFileStore.CopyThroughTemporaryFile(file, conflict);
            }

            baseline.Add(relative, true);
            updated = true;
        }

        if (updated && FileSystemSafety.IsDirectoryInside(bootstrap, profileWorkspace))
            ProfileWritableGameFileStore.WriteBaselineFile(bootstrap, ManifestName, baseline);
    }

    public static void PreserveBeforeClear(string bootstrap, string profileWorkspace, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(bootstrap)) return;
        FileSystemSafety.EnsureNoDirectoryLinks(bootstrap);
        if (File.Exists(Path.Combine(bootstrap, ManifestName)))
        {
            Capture(bootstrap, profileWorkspace, cancellationToken);
            return;
        }

        // An older cache has no inventory. Keep it available for manual recovery.
        if (!Directory.EnumerateFileSystemEntries(bootstrap).Any()) return;
        var diagnostics = Path.Combine(profileWorkspace, "diagnostics");
        FileSystemSafety.EnsureNoDirectoryLinks(diagnostics);
        var recovery = Path.Combine(diagnostics, $"bootstrap-recovery-{Guid.NewGuid():N}");
        foreach (var file in Enumerate(bootstrap))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileWritableGameFileStore.CopyThroughTemporaryFile(file,
                Path.Combine(recovery, Path.GetRelativePath(bootstrap, file)));
        }
    }

    private static IEnumerable<string> Enumerate(string bootstrap) =>
        Directory.EnumerateFiles(bootstrap, "*", RegularFiles).Where(file =>
            !Path.GetRelativePath(bootstrap, file).Equals(ManifestName, StringComparison.OrdinalIgnoreCase));
}
