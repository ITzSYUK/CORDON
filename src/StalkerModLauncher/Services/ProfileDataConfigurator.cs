namespace StalkerModLauncher.Services;

internal static class ProfileDataConfigurator
{
    public static string Configure(
        string gamePath,
        string currentWorkspace,
        string profileWorkspace,
        IProgress<string> progress,
        FileLayerPlan? layerPlan = null,
        CancellationToken cancellationToken = default)
    {
        var fsgameDir = FindFileDirectory(currentWorkspace, "fsgame.ltx");
        if (fsgameDir is null)
        {
            throw new FileNotFoundException(
                "fsgame.ltx was not found in the workspace. Profile-local saves and logs cannot be guaranteed.");
        }

        var relativeDir = Path.GetRelativePath(currentWorkspace, fsgameDir);
        var workingDirectoryRelative = relativeDir == "." ? string.Empty : relativeDir;
        if (workingDirectoryRelative.Length > 0)
        {
            progress.Report($"Detected fsgame.ltx in '{relativeDir}' — using as working directory.");
        }

        var profileDataPath = layerPlan?.GameDataRoot ?? Path.Combine(profileWorkspace, "userdata");
        var fsgamePath = Path.Combine(fsgameDir, "fsgame.ltx");
        WriteProfileFsgame(fsgamePath, fsgamePath, profileDataPath);
        Directory.CreateDirectory(profileDataPath);
        if (layerPlan?.UsesSharedGameData == true)
        {
            progress.Report($"Используются общие данные базовой игры: {profileDataPath}. Существующие файлы не копируются и не заменяются.");
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

        progress.Report("fsgame.ltx rewritten for profile-local saves and logs.");
        return workingDirectoryRelative;
    }

    internal static void WriteProfileFsgame(string sourcePath, string destinationPath, string profileDataPath)
    {
        WriteProfileFsgameDefinition(sourcePath, destinationPath, $"true | false| {profileDataPath}");
    }

    private static void WriteProfileFsgameDefinition(string sourcePath, string destinationPath, string appDataDefinition)
    {
        var lines = File.ReadAllLines(sourcePath, XRayTextEncoding.Config);
        var appDataLineIndex = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith("$app_data_root$", StringComparison.OrdinalIgnoreCase));
        if (appDataLineIndex < 0)
        {
            throw new InvalidDataException(
                $"fsgame.ltx does not contain $app_data_root$: {sourcePath}. " +
                "Profile-local saves and logs cannot be guaranteed, so launch was blocked.");
        }

        lines[appDataLineIndex] = $"$app_data_root$ = {appDataDefinition}";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            // The workspace entry can be a link to a read-only source. Recreate the entry
            // instead of ever writing through that link into the game or a mod folder.
            File.Delete(destinationPath);
        }

        File.WriteAllLines(destinationPath, lines, XRayTextEncoding.Config);
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
            [("base game", ProfileAppDataSourceLocator.EnumerateRoots(gamePath))],
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
                progress?.Report("Keeping existing profile-local user.ltx.");
                return;
            }

            var stillMatchesLowerLayer = candidates
                .Skip(1)
                .Any(candidate => FilesAreEqual(candidate.Path, destination));
            if (!stillMatchesLowerLayer)
            {
                progress?.Report("Keeping modified profile-local user.ltx.");
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

            progress?.Report($"Profile user.ltx prepared from {selected.SourceName}: {selected.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Report($"Warning: could not copy user.ltx from {selected.Path}: {ex.Message}");
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
