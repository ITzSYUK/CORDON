using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class ProfileDataConfigurator
{
    private const string LegacyManualDataMigrationMarkerFileName = ".stalker-launcher-manual-data-migrated";

    internal static string MissingFsgameMessage =>
        LocalizedText.Format(Strings.Fsgame_NotFoundHelpFormat, Environment.NewLine);

    public static string Configure(
        string gamePath,
        string currentWorkspace,
        string profileWorkspace,
        IProgress<string> progress,
        FileLayerPlan? layerPlan = null,
        CancellationToken cancellationToken = default)
    {
        var manualSource = layerPlan?.ManualFsgameSource;
        var selectedSource = manualSource ??
                             (layerPlan?.UsesFsgameLaunchArgument == true ? layerPlan.FindFsgameSource() : null);
        var fsgamePath = selectedSource is null
            ? FindFileDirectory(currentWorkspace, "fsgame.ltx") is { } directory
                ? Path.Combine(directory, "fsgame.ltx")
                : null
            : FileSystemSafety.ResolvePathInside(
                currentWorkspace,
                selectedSource.RelativePath,
                Strings.Safety_ProfileFsgame);
        if (fsgamePath is null)
        {
            throw new FileNotFoundException(MissingFsgameMessage);
        }

        var fsgameDir = Path.GetDirectoryName(fsgamePath)!;
        var relativeDir = Path.GetRelativePath(currentWorkspace, fsgameDir);
        var workingDirectoryRelative = layerPlan?.UsesFsgameLaunchArgument == true || relativeDir == "."
            ? string.Empty
            : relativeDir;
        if (workingDirectoryRelative.Length > 0)
        {
            progress.Report(LocalizedText.Format(Strings.Progress_FsgameDetectedFormat, relativeDir));
        }

        var profileDataPath = layerPlan?.GameDataRoot ?? Path.Combine(profileWorkspace, "userdata");
        if (layerPlan is not null)
        {
            MigrateLegacyManualData(
                layerPlan,
                Path.Combine(profileWorkspace, "userdata", "overwrite"),
                progress);
        }
        var sourcePath = selectedSource?.FullPath ?? fsgamePath;
        WriteProfileFsgame(sourcePath, fsgamePath, profileDataPath);
        if (layerPlan?.UsesFsgameLaunchArgument == true)
        {
            var launchArgumentPath = FileSystemSafety.ResolvePathInside(
                currentWorkspace,
                layerPlan.FsgameLaunchRelativePath,
                Strings.Fsgame_ArgumentFile);
            if (!launchArgumentPath.Equals(fsgamePath, StringComparison.OrdinalIgnoreCase))
            {
                WriteProfileFsgame(sourcePath, launchArgumentPath, profileDataPath);
            }
        }
        if (manualSource is not null)
        {
            progress.Report(LocalizedText.Format(Strings.Fsgame_ManualUsingFormat, manualSource.FullPath));
        }
        Directory.CreateDirectory(profileDataPath);
        if (layerPlan?.UsesSharedGameData == true)
        {
            progress.Report(LocalizedText.Format(Strings.ProfileData_SharedFormat, profileDataPath));
            return workingDirectoryRelative;
        }
        if (layerPlan is null)
        {
            EnsureProfileUserLtx(gamePath, profileDataPath, progress);
        }
        else
        {
            EnsureProfileUserLtx(layerPlan, profileDataPath, progress);
        }
        if (layerPlan is not null)
        {
            ProfileShaderCacheSeeder.Seed(layerPlan, profileDataPath, progress, cancellationToken);
        }

        progress.Report(Strings.Progress_FsgameRewritten);
        return workingDirectoryRelative;
    }

    internal static void MigrateLegacyManualData(
        FileLayerPlan layerPlan,
        string writeOverlayRoot,
        IProgress<string>? progress)
    {
        if (layerPlan.UsesSharedGameData || layerPlan.ManualFsgameSource is not { } source)
        {
            return;
        }

        var baseRoot = Path.GetFullPath(layerPlan.BaseGame.RootPath);
        var configuredRoot = ProfileAppDataSourceLocator.ResolveConfiguredRootFromFile(source.FullPath, baseRoot);
        if (FileSystemSafety.IsSameDirectory(configuredRoot, baseRoot) ||
            !FileSystemSafety.IsDirectoryInside(configuredRoot, baseRoot))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(baseRoot, configuredRoot);
        var legacyRoot = FileSystemSafety.ResolvePathInside(
            writeOverlayRoot,
            relativePath,
            Strings.Safety_LegacyProfileData);
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        var markerPath = Path.Combine(legacyRoot, LegacyManualDataMigrationMarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        var result = GameDataCopyService.CopyMissingFromProfileOverwrite(legacyRoot, layerPlan.GameDataRoot);
        File.WriteAllText(markerPath, string.Empty);
        progress?.Report(
            LocalizedText.Format(Strings.ProfileData_MigratedFormat, result.Copied, result.Skipped));
    }

    internal static void WriteProfileFsgame(string sourcePath, string destinationPath, string profileDataPath)
    {
        var appDataDefinition = $"true | false| {profileDataPath}";
        WriteProfileFsgameDefinition(sourcePath, destinationPath, appDataDefinition);
        if (!Path.GetFileName(destinationPath).Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase))
        {
            WriteProfileFsgameDefinition(
                sourcePath,
                Path.Combine(Path.GetDirectoryName(destinationPath)!, "fsgame.ltx"),
                appDataDefinition);
        }
    }

    private static void WriteProfileFsgameDefinition(string sourcePath, string destinationPath, string appDataDefinition)
    {
        var (lines, appDataLineIndex) = ReadValidatedFsgame(sourcePath);
        lines[appDataLineIndex] = $"$app_data_root$ = {appDataDefinition}";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
        {
            // The workspace entry can be a link to a read-only source. Recreate the entry
            // instead of ever writing through that link into the game or a mod folder.
            File.Delete(destinationPath);
        }

        File.WriteAllLines(destinationPath, lines, XRayTextEncoding.Config);
    }

    internal static void ValidateFsgameSource(string sourcePath) => _ = ReadValidatedFsgame(sourcePath);

    private static (string[] Lines, int AppDataLineIndex) ReadValidatedFsgame(string sourcePath)
    {
        var lines = File.ReadAllLines(sourcePath, XRayTextEncoding.Config);
        var appDataLineIndex = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith("$app_data_root$", StringComparison.OrdinalIgnoreCase));
        if (appDataLineIndex < 0)
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Fsgame_AppDataMissingFormat, sourcePath));
        }

        return (lines, appDataLineIndex);
    }

    public static string? FindFileDirectory(string searchRoot, string fileName)
    {
        var rootFile = Path.Combine(searchRoot, fileName);
        if (File.Exists(rootFile)) return searchRoot;
        foreach (var dir in Directory.EnumerateDirectories(searchRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(dir, fileName))) return dir;
        }
        foreach (var dir in Directory.EnumerateDirectories(searchRoot, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(Path.Combine(subDir, fileName))) return subDir;
            }
        }
        return null;
    }

    public static void EnsureProfileUserLtx(string gamePath, string profileDataPath, IProgress<string>? progress)
    {
        EnsureProfileUserLtx(
            [(Strings.Layer_BaseGame, ProfileAppDataSourceLocator.EnumerateRoots(gamePath))],
            profileDataPath,
            progress);
    }

    public static void EnsureProfileUserLtx(
        FileLayerPlan layerPlan,
        string profileDataPath,
        IProgress<string>? progress)
    {
        var sources = layerPlan.SourceLayers
            .OrderByDescending(layer => layer.Order)
            .Select(layer => (
                FileLayerPlan.GetDisplayName(layer),
                ProfileAppDataSourceLocator.EnumerateRoots(layer)));
        EnsureProfileUserLtx(sources, profileDataPath, progress);
    }

    private static void EnsureProfileUserLtx(
        IEnumerable<(string SourceName, IEnumerable<string> Roots)> sources,
        string profileDataPath,
        IProgress<string>? progress)
    {
        var destination = Path.Combine(profileDataPath, "user.ltx");
        var candidates = sources
            .SelectMany(source => source.Roots.Select(root => new UserLtxSource(
                Path.Combine(root, "user.ltx"),
                source.SourceName)))
            .Where(source => File.Exists(source.Path))
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var selected = candidates[0];
        if (File.Exists(destination))
        {
            if (FilesAreEqual(selected.Path, destination))
            {
                progress?.Report(Strings.Progress_KeepingExistingUserLtx);
                return;
            }

            var stillMatchesLowerLayer = candidates
                .Skip(1)
                .Any(candidate => FilesAreEqual(candidate.Path, destination));
            if (!stillMatchesLowerLayer)
            {
                progress?.Report(Strings.Progress_KeepingModifiedUserLtx);
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(profileDataPath);
            var temporary = destination + $".launcher-{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(selected.Path, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            progress?.Report(LocalizedText.Format(Strings.Progress_UserLtxPreparedFormat, selected.SourceName, selected.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Report(LocalizedText.Format(Strings.Progress_UserLtxCopyFailedFormat, selected.Path, ex.Message));
        }
    }

    private static bool FilesAreEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        using var firstStream = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        using var secondStream = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            var firstRead = firstStream.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = secondStream.Read(secondBuffer, 0, secondBuffer.Length);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }

    private sealed record UserLtxSource(string Path, string SourceName);
}
