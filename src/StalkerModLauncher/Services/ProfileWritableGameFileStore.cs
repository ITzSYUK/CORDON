using System.Security.Cryptography;
using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class ProfileWritableGameFileStore
{
    private const string StoreDirectoryName = "writable-game-files";
    private const string WorkspaceDeletionsFileName = ".workspace-writable-deletions.json";
    internal const string BaselineFileName = ".writable-baseline.json";

    public static string[] CaptureFromWorkspace(string currentWorkspace, string profileWorkspace,
        IProgress<string>? progress = null, bool trackDeletions = false)
    {
        if (!Directory.Exists(currentWorkspace))
        {
            return [];
        }

        FileSystemSafety.EnsureNoDirectoryLinks(currentWorkspace);
        FileSystemSafety.EnsureNoDirectoryLinks(profileWorkspace);
        DeleteLegacyStoredFsgame(profileWorkspace, progress);

        var baselinePath = Path.Combine(currentWorkspace, BaselineFileName);
        Dictionary<string, string?>? baseline = null;
        if (File.Exists(baselinePath))
        {
            try { baseline = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(baselinePath)); }
            catch (JsonException ex) { throw new InvalidDataException(Strings.Writable_BaselineInvalid, ex); }
            if (baseline is null) throw new InvalidDataException(Strings.Writable_BaselineInvalid);
        }
        var builtInPaths = ProfileWritableGameFiles.Rules.Select(rule => rule.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackedPaths = builtInPaths.Concat(baseline?.Keys.AsEnumerable() ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var baselineChanged = false;
        var deletions = trackDeletions && baseline is not null ? ReadWorkspaceDeletions(profileWorkspace) : null;
        var deletionsChanged = false;
        var captured = 0;
        var skippedAsStale = 0;
        foreach (var relativePath in trackedPaths)
        {
            var workspaceFile = FileSystemSafety.ResolvePathInside(currentWorkspace, relativePath, Strings.Safety_ProfileWritableFile);
            var isModWritableFile = !builtInPaths.Contains(relativePath);
            if (isModWritableFile && (!relativePath.StartsWith(@"gamedata\", StringComparison.OrdinalIgnoreCase) || relativePath.Contains(':')))
                throw new InvalidDataException(Strings.Writable_BaselineInvalid);

            if (isModWritableFile)
                FileSystemSafety.EnsureNoDirectoryLinks(Path.GetDirectoryName(workspaceFile)!);
            var storedFile = GetStoredFilePath(profileWorkspace, relativePath);
            if (!File.Exists(workspaceFile))
            {
                if (deletions is not null && baseline!.TryGetValue(relativePath, out var deletedHash) && deletedHash is not null)
                {
                    if (!File.Exists(storedFile) || Hash(storedFile) == deletedHash)
                    {
                        deletions[relativePath] = deletedHash;
                        deletionsChanged = true;
                    }
                    baseline[relativePath] = null;
                    baselineChanged = true;
                }
                continue;
            }

            if (isModWritableFile && (File.GetAttributes(workspaceFile) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, workspaceFile));

            string? runtimeHash = null;
            if (baseline?.TryGetValue(relativePath, out var originalHash) == true)
            {
                runtimeHash = Hash(workspaceFile);
                if (runtimeHash == originalHash) continue;
                if (File.Exists(storedFile))
                {
                    var storedHash = Hash(storedFile);
                    if (storedHash == runtimeHash)
                    {
                        baseline[relativePath] = runtimeHash;
                        baselineChanged = true;
                        deletionsChanged |= deletions?.Remove(relativePath) == true;
                        continue;
                    }
                    if (storedHash != originalHash)
                    {
                        var backup = Path.Combine(profileWorkspace, "diagnostics", $"{Path.GetFileName(storedFile)}.conflict-{Guid.NewGuid():N}.bak");
                        CopyThroughTemporaryFile(storedFile, backup);
                        progress?.Report(LocalizedText.Format(Strings.Writable_ConflictArchivedFormat, backup));
                    }
                }
            }
            else if (File.Exists(storedFile))
            {
                var writeTimeComparison = File.GetLastWriteTimeUtc(workspaceFile)
                    .CompareTo(File.GetLastWriteTimeUtc(storedFile));
                if (writeTimeComparison <= 0)
                {
                    if (writeTimeComparison < 0)
                    {
                        skippedAsStale++;
                    }

                    continue;
                }
            }

            CopyThroughTemporaryFile(workspaceFile, storedFile);
            deletionsChanged |= deletions?.Remove(relativePath) == true;
            captured++;
            if (baseline is not null)
            {
                baseline[relativePath] = runtimeHash ?? Hash(workspaceFile);
                baselineChanged = true;
            }
        }

        if (deletionsChanged)
        {
            var userData = Path.Combine(profileWorkspace, "userdata");
            FileSystemSafety.EnsureNoDirectoryLinks(userData);
            Directory.CreateDirectory(userData);
            WriteBaselineFile(userData, WorkspaceDeletionsFileName, deletions!);
        }
        if (baselineChanged && FileSystemSafety.IsDirectoryInside(currentWorkspace, profileWorkspace))
            WriteBaseline(currentWorkspace, baseline!);
        if (captured > 0)
            progress?.Report(LocalizedText.Format(Strings.Writable_CapturedFormat, captured));

        if (skippedAsStale > 0)
        {
            progress?.Report(
                LocalizedText.Format(Strings.Writable_StaleSkippedFormat, skippedAsStale));
        }
        return trackedPaths;
    }

    public static void RecordBootstrapState(string runtimeRoot, IEnumerable<string>? writablePaths = null)
    {
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in writablePaths ?? ProfileWritableGameFiles.Rules.Select(rule => rule.RelativePath))
        {
            var path = Path.Combine(runtimeRoot, relativePath);
            if (File.Exists(path))
                hashes[relativePath] = Hash(path);
            else if (writablePaths is not null)
                hashes[relativePath] = null;
        }
        WriteBaseline(runtimeRoot, hashes);
    }

    private static void WriteBaseline(string runtimeRoot, Dictionary<string, string?> hashes) =>
        WriteBaselineFile(runtimeRoot, BaselineFileName, hashes);

    internal static void WriteBaselineFile<T>(string runtimeRoot, string fileName, Dictionary<string, T> hashes)
    {
        var destination = Path.Combine(runtimeRoot, fileName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(hashes));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Prepare(
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string profileWorkspace,
        IProgress<string>? progress = null,
        bool linkedWorkspace = false,
        CancellationToken cancellationToken = default)
    {
        var bootstrap = Path.Combine(profileWorkspace, ".usvfs-bootstrap");
        UsvfsBootstrapFileStore.Capture(bootstrap, profileWorkspace, cancellationToken);
        var seeded = 0;
        var migrated = 0;
        foreach (var file in manifest.WritableFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storedFile = file.StoragePath;
            var overwriteFile = Path.Combine(
                profileWorkspace,
                ProfileWritableGameFiles.DefaultOverwriteRootRelativePath,
                file.RelativePath);
            var overwriteDirectory = Path.GetDirectoryName(overwriteFile)!;
            if (File.Exists(overwriteFile))
            {
                FileSystemSafety.EnsureNoDirectoryLinks(overwriteDirectory);
                if (!File.Exists(storedFile) || File.GetLastWriteTimeUtc(overwriteFile) >= File.GetLastWriteTimeUtc(storedFile))
                    CopyThroughTemporaryFile(overwriteFile, storedFile);
                File.Delete(overwriteFile);
                migrated++;
                continue;
            }

            if (File.Exists(storedFile))
            {
                continue;
            }

            var source = layerPlan.FindFinalFile(file.RelativePath);
            if (source is null)
            {
                // Class variants may introduce a path absent from the initial game.
                // Expose its parent for CreateTarget without inventing file contents.
                if (!linkedWorkspace)
                {
                    FileSystemSafety.EnsureNoDirectoryLinks(overwriteDirectory);
                    Directory.CreateDirectory(overwriteDirectory);
                }
                continue;
            }

            CopyThroughTemporaryFile(source.FullPath, storedFile);
            seeded++;
        }

        if (seeded > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Writable_UsvfsPreparedFormat, seeded));
        }

        if (migrated > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Writable_UsvfsMigratedFormat, migrated));
        }

        if (linkedWorkspace)
            DeleteLegacyStoredFsgame(profileWorkspace, progress);
        CaptureLinkedWorkspace(profileWorkspace, profileWorkspace, linkedWorkspace, progress, cancellationToken);

        // The bootstrap is recreated on launch; collect files replaced there before discarding it.
        CaptureFromWorkspace(bootstrap, profileWorkspace, progress);
    }

    public static void CaptureLinkedWorkspace(
        string workspace,
        string destination,
        bool includeLegacy,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var current = Path.Combine(workspace, "current");
        if (!Directory.Exists(current)) return;
        var known = includeLegacy || File.Exists(Path.Combine(current, BaselineFileName))
            ? CaptureFromWorkspace(current, destination, progress, trackDeletions: true)
            : ProfileWritableGameFiles.Rules.Select(rule => rule.RelativePath).ToArray();
        WorkspaceOverwriteStore.Capture(workspace, destination, known, cancellationToken);
    }

    public static void RestoreToWorkspace(
        string currentWorkspace,
        IReadOnlyList<OverlayWritableFileSnapshot> writableFiles,
        IReadOnlySet<string> deletedPaths,
        IProgress<string>? progress,
        WorkspaceBuildStats? stats = null)
    {
        var restored = 0;
        foreach (var file in writableFiles)
        {
            var relativePath = file.RelativePath;
            var workspaceFile = Path.Combine(currentWorkspace, relativePath);
            var parent = Path.GetDirectoryName(workspaceFile)!;
            FileSystemSafety.EnsureNoDirectoryLinks(parent);
            Directory.CreateDirectory(parent);
            var storedFile = file.StoragePath;
            if (deletedPaths.Contains(relativePath))
            {
                File.Delete(workspaceFile);
                continue;
            }
            if (!File.Exists(storedFile))
            {
                continue;
            }

            CopyThroughTemporaryFile(storedFile, workspaceFile);
            stats?.RecordRequiredLocal(relativePath, new FileInfo(workspaceFile).Length);
            restored++;
        }

        if (restored > 0)
        {
            var message = stats is null ? Strings.Writable_LaunchPreparedFormat : Strings.Writable_WorkspaceRestoredFormat;
            progress?.Report(LocalizedText.Format(message, restored));
        }
    }

    internal static HashSet<string> GetDeletedWorkspacePaths(string workspace, IReadOnlyList<OverlayWritableFileSnapshot> writableFiles)
    {
        var deletions = ReadWorkspaceDeletions(workspace);
        return writableFiles.Where(file => deletions.TryGetValue(file.RelativePath, out var deletedHash) &&
                (!File.Exists(file.StoragePath) || Hash(file.StoragePath) == deletedHash))
            .Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadWorkspaceDeletions(string profileWorkspace)
    {
        var path = Path.Combine(profileWorkspace, "userdata", WorkspaceDeletionsFileName);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        FileSystemSafety.EnsureNoDirectoryLinks(Path.GetDirectoryName(path)!);
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? throw new InvalidDataException(Strings.Writable_BaselineInvalid), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex) { throw new InvalidDataException(Strings.Writable_BaselineInvalid, ex); }
    }

    internal static string GetStoredFilePath(string profileWorkspace, string relativePath)
    {
        FileSystemSafety.EnsureRelativePath(relativePath, Strings.Safety_ProfileWritableFile);
        return Path.Combine(profileWorkspace, "userdata", StoreDirectoryName, relativePath);
    }

    private static void DeleteLegacyStoredFsgame(string profileWorkspace, IProgress<string>? progress)
    {
        var legacyPath = Path.Combine(profileWorkspace, "userdata", StoreDirectoryName, "fsgame.ltx");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        FileSystemSafety.EnsureNoDirectoryLinks(Path.GetDirectoryName(legacyPath)!);
        File.Delete(legacyPath);
        progress?.Report(Strings.Writable_ObsoleteFsgameRemoved);
    }

    internal static void CopyThroughTemporaryFile(string sourceFile, string destinationFile)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationFile)!;
        FileSystemSafety.EnsureNoDirectoryLinks(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var temporaryFile = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceFile, temporaryFile, overwrite: false);
            ClearReadOnlyAttribute(temporaryFile);
            File.Move(temporaryFile, destinationFile, overwrite: true);
            ClearReadOnlyAttribute(destinationFile);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static void ClearReadOnlyAttribute(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
