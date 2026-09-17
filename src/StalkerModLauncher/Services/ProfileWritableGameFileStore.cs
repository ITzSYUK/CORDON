using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class ProfileWritableGameFileStore
{
    private const string StoreDirectoryName = "writable-game-files";

    public static void CaptureFromWorkspace(string currentWorkspace, string profileWorkspace, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(currentWorkspace))
        {
            return;
        }

        DeleteLegacyStoredFsgame(profileWorkspace, progress);

        var captured = 0;
        var skippedAsStale = 0;
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var relativePath = rule.RelativePath;
            var workspaceFile = Path.Combine(currentWorkspace, relativePath);
            if (!File.Exists(workspaceFile))
            {
                continue;
            }

            var storedFile = GetStoredFilePath(profileWorkspace, relativePath);
            if (File.Exists(storedFile))
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

            Directory.CreateDirectory(Path.GetDirectoryName(storedFile)!);
            CopyThroughTemporaryFile(workspaceFile, storedFile);
            captured++;
        }

        if (captured > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Writable_CapturedFormat, captured));
        }

        if (skippedAsStale > 0)
        {
            progress?.Report(
                LocalizedText.Format(Strings.Writable_StaleSkippedFormat, skippedAsStale));
        }
    }

    public static void EnsureWorkspaceDirectories(string currentWorkspace)
    {
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var relativePath = rule.RelativePath;
            var workspaceFile = Path.Combine(currentWorkspace, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        }
    }

    public static void PrepareForVirtualFileSystem(
        FileLayerPlan layerPlan,
        string profileWorkspace,
        IProgress<string>? progress = null)
    {
        var seeded = 0;
        var migrated = 0;
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var storedFile = GetStoredFilePath(profileWorkspace, rule.RelativePath);
            var overwriteFile = Path.Combine(
                profileWorkspace,
                ProfileWritableGameFiles.DefaultOverwriteRootRelativePath,
                rule.RelativePath);
            if (File.Exists(overwriteFile))
            {
                CopyThroughTemporaryFile(overwriteFile, storedFile);
                File.Delete(overwriteFile);
                migrated++;
                continue;
            }

            if (File.Exists(storedFile))
            {
                continue;
            }

            var source = layerPlan.FindFinalFile(rule.RelativePath);
            if (source is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(storedFile)!);
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
    }

    public static void RestoreToCachedWorkspace(
        string currentWorkspace,
        string profileWorkspace,
        IProgress<string>? progress = null)
    {
        EnsureWorkspaceDirectories(currentWorkspace);
        DeleteLegacyStoredFsgame(profileWorkspace, progress);

        var restored = 0;
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var relativePath = rule.RelativePath;
            var storedFile = GetStoredFilePath(profileWorkspace, relativePath);
            if (!File.Exists(storedFile))
            {
                continue;
            }

            var workspaceFile = Path.Combine(currentWorkspace, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
            CopyIndependentWorkspaceFile(storedFile, workspaceFile, relativePath, stats: null);
            restored++;
        }

        if (restored > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Writable_LaunchPreparedFormat, restored));
        }
    }

    public static void RestoreToWorkspace(
        string currentWorkspace,
        string profileWorkspace,
        WorkspaceBuildStats stats,
        IProgress<string>? progress = null)
    {
        EnsureWorkspaceDirectories(currentWorkspace);
        DeleteLegacyStoredFsgame(profileWorkspace, progress);

        var restored = 0;
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var relativePath = rule.RelativePath;
            var workspaceFile = Path.Combine(currentWorkspace, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);

            var storedFile = GetStoredFilePath(profileWorkspace, relativePath);
            if (!File.Exists(storedFile))
            {
                continue;
            }

            CopyIndependentWorkspaceFile(storedFile, workspaceFile, relativePath, stats);
            restored++;
        }

        if (restored > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Writable_WorkspaceRestoredFormat, restored));
        }
    }

    private static string GetStoredFilePath(string profileWorkspace, string relativePath)
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

        File.Delete(legacyPath);
        progress?.Report(Strings.Writable_ObsoleteFsgameRemoved);
    }

    private static void CopyThroughTemporaryFile(string sourceFile, string destinationFile)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationFile)!;
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

    private static void CopyIndependentWorkspaceFile(
        string sourceFile,
        string workspaceFile,
        string relativePath,
        WorkspaceBuildStats? stats)
    {
        if (File.Exists(workspaceFile))
        {
            File.Delete(workspaceFile);
        }

        File.Copy(sourceFile, workspaceFile, overwrite: false);
        ClearReadOnlyAttribute(workspaceFile);
        stats?.RecordRequiredLocal(relativePath, new FileInfo(workspaceFile).Length);
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
