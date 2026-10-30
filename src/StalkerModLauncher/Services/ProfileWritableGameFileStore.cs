using System.Security.Cryptography;
using System.Text.Json;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class ProfileWritableGameFileStore
{
    private const string StoreDirectoryName = "writable-game-files";
    private const string BaselineFileName = ".writable-baseline.json";

    public static void CaptureFromWorkspace(string currentWorkspace, string profileWorkspace, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(currentWorkspace))
        {
            return;
        }

        DeleteLegacyStoredFsgame(profileWorkspace, progress);

        var baselinePath = Path.Combine(currentWorkspace, BaselineFileName);
        Dictionary<string, string>? baseline = null;
        if (File.Exists(baselinePath))
        {
            try { baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(baselinePath)); }
            catch (JsonException ex) { throw new InvalidDataException(Strings.Writable_BaselineInvalid, ex); }
            if (baseline is null) throw new InvalidDataException(Strings.Writable_BaselineInvalid);
        }
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
            if (baseline?.TryGetValue(relativePath, out var originalHash) == true)
            {
                var runtimeHash = Hash(workspaceFile);
                if (runtimeHash == originalHash) continue;
                if (File.Exists(storedFile))
                {
                    var storedHash = Hash(storedFile);
                    if (storedHash == runtimeHash) continue;
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

    public static void RecordBootstrapState(string bootstrapRoot)
    {
        var hashes = ProfileWritableGameFiles.Rules
            .Where(rule => File.Exists(Path.Combine(bootstrapRoot, rule.RelativePath)))
            .ToDictionary(rule => rule.RelativePath, rule => Hash(Path.Combine(bootstrapRoot, rule.RelativePath)));
        var destination = Path.Combine(bootstrapRoot, BaselineFileName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(hashes));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Prepare(
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

        // The bootstrap is recreated on launch; collect files replaced there before discarding it.
        CaptureFromWorkspace(Path.Combine(profileWorkspace, ".usvfs-bootstrap"), profileWorkspace, progress);
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

    private static void ClearReadOnlyAttribute(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
