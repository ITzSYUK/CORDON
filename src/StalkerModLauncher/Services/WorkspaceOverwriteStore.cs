using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class WorkspaceOverwriteStore
{
    private const string BaselineFile = ".workspace-overwrite-baseline.json";
    private static readonly EnumerationOptions SafeFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static void Capture(
        string workspace, string destination, IReadOnlyCollection<string> knownWritablePaths,
        CancellationToken cancellationToken)
    {
        var current = Path.Combine(workspace, "current");
        FileSystemSafety.EnsureNoDirectoryLinks(current);
        var manifest = WorkspaceManifestStore.Read(workspace);
        if (manifest?.Fingerprint is not { } fingerprint)
            return;
        if (fingerprint.Sources is not { Count: > 0 })
            throw new InvalidDataException(Strings.WorkspaceManifest_CacheUnreadable);

        var writablePaths = knownWritablePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in fingerprint.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var excluded = layer.ExcludedFiles.Select(path => path.Replace('/', '\\').TrimStart('\\'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in layer.Files)
            {
                if (!excluded.Contains(file.RelativePath))
                    originalPaths.Add(file.RelativePath);
            }

            if (Path.IsPathFullyQualified(fingerprint.FsgameSourcePath) &&
                FileSystemSafety.IsDirectoryInside(fingerprint.FsgameSourcePath, layer.RootPath))
                originalPaths.Add(Path.GetRelativePath(layer.RootPath, fingerprint.FsgameSourcePath));
        }
        originalPaths.Add(fingerprint.FsgameLaunchRelativePath);

        var baseline = ReadBaseline(current);
        var updated = false;
        var overwrite = GetRoot(destination);
        foreach (var file in Directory.EnumerateFiles(current, "*", SafeFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(current, file);
            if (IsServiceFile(relative) || writablePaths.Contains(relative))
                continue;
            var tracked = baseline.TryGetValue(relative, out var initialHash);
            if (!tracked && originalPaths.Contains(relative))
                continue;

            var hash = ProfileWritableGameFileStore.Hash(file);
            if (tracked && hash == initialHash)
                continue;

            var stored = Resolve(overwrite, relative);
            FileSystemSafety.EnsureNoDirectoryLinks(Path.GetDirectoryName(stored)!);
            if (File.Exists(stored))
            {
                if ((File.GetAttributes(stored) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, stored));

                var storedHash = ProfileWritableGameFileStore.Hash(stored);
                if (storedHash != hash && storedHash != initialHash)
                {
                    var backup = Path.Combine(destination, "diagnostics",
                        $"{Path.GetFileName(stored)}.conflict-{Guid.NewGuid():N}.bak");
                    ProfileWritableGameFileStore.CopyThroughTemporaryFile(stored, backup);
                }
                if (storedHash != hash)
                    ProfileWritableGameFileStore.CopyThroughTemporaryFile(file, stored);
            }
            else
            {
                ProfileWritableGameFileStore.CopyThroughTemporaryFile(file, stored);
            }
            baseline[relative] = hash;
            updated = true;
        }
        if (updated && FileSystemSafety.IsSameDirectory(workspace, destination))
            WriteBaseline(current, baseline);
    }

    public static bool NeedsRebuild(string current, string workspace, FileLayerPlan plan) =>
        ReadBaseline(current).Keys.Any(relative =>
            !CanRestore(relative, plan) || !File.Exists(Resolve(GetRoot(workspace), relative)));

    public static void Restore(
        string current, OverlayManifest manifest, FileLayerPlan plan, CancellationToken cancellationToken,
        WorkspaceBuildStats? stats = null)
    {
        var overwrite = manifest.WriteOverlayRoot;
        FileSystemSafety.EnsureNoDirectoryLinks(overwrite);
        var baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(overwrite))
        {
            var writable = manifest.WritableFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(overwrite, "*", SafeFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(overwrite, file);
                if (!CanRestore(relative, plan) || writable.Contains(relative) ||
                    manifest.Executable is { IsPinned: true } executable &&
                    relative.Equals(executable.RelativePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Resolve(current, relative);
                ProfileWritableGameFileStore.CopyThroughTemporaryFile(file, target);
                baseline[relative] = ProfileWritableGameFileStore.Hash(target);
                stats?.RecordRequiredLocal(relative, new FileInfo(target).Length);
            }
        }
        if (baseline.Count > 0 || File.Exists(Path.Combine(current, BaselineFile)))
            WriteBaseline(current, baseline);
    }

    private static bool CanRestore(string relative, FileLayerPlan plan) =>
        !IsServiceFile(relative) &&
        !(plan.UsesAnomalyAppDataAlias && AnomalyAppDataAlias.IsAppDataPath(relative)) &&
        !(plan.UsesSharedGameData && FileSystemSafety.IsDirectoryInside(
            Resolve(plan.BaseGame.RootPath, relative), plan.GameDataRoot)) &&
        !string.Equals(relative, plan.ManualFsgameSource?.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        !(plan.UsesFsgameLaunchArgument &&
          relative.Equals(plan.FsgameLaunchRelativePath, StringComparison.OrdinalIgnoreCase));

    private static bool IsServiceFile(string relative) =>
        Path.GetFileName(relative).Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(BaselineFile, StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(ProfileWritableGameFileStore.BaselineFileName, StringComparison.OrdinalIgnoreCase);

    private static string GetRoot(string workspace) =>
        Path.Combine(workspace, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);

    private static string Resolve(string root, string relative) =>
        FileSystemSafety.ResolvePathInside(root, relative, Strings.Safety_ProfileWritableFile);

    private static Dictionary<string, string> ReadBaseline(string current)
    {
        var path = Path.Combine(current, BaselineFile);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException(Strings.Writable_BaselineInvalid),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Strings.Writable_BaselineInvalid, ex);
        }
    }

    private static void WriteBaseline(string current, Dictionary<string, string> baseline) =>
        ProfileWritableGameFileStore.WriteBaselineFile(current, BaselineFile, baseline);
}
