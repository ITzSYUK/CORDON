using StalkerModLauncher.Models;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed record Mo2ProfileSource(string Name, string DirectoryPath, string ModListPath);

public sealed record Mo2ImportDiscovery(
    string RootPath,
    string ProfilesPath,
    string ModsPath,
    string OverwritePath,
    string GamePath,
    IReadOnlyList<Mo2ProfileSource> Profiles,
    Mo2ProfileSource? SelectedProfile);

public sealed class Mo2ImportPreviewEntry : ObservableObject
{
    private string _sourcePath;

    public Mo2ImportPreviewEntry(
        int order,
        string name,
        string sourcePath,
        bool isEnabled,
        string groupName,
        bool isOverwrite,
        IReadOnlyList<string> candidatePaths)
    {
        Order = order;
        Name = name;
        IsEnabled = isEnabled;
        GroupName = groupName;
        IsOverwrite = isOverwrite;
        CandidatePaths = candidatePaths;
        CandidateOptions = candidatePaths.Select(path => new Mo2FolderCandidate(path)).ToArray();
        _sourcePath = candidatePaths.Count == 1 ? candidatePaths[0] : sourcePath;
    }

    public int Order { get; }
    public string Name { get; }
    public bool IsEnabled { get; }
    public string GroupName { get; }
    public bool IsOverwrite { get; }
    public IReadOnlyList<string> CandidatePaths { get; }
    public IReadOnlyList<Mo2FolderCandidate> CandidateOptions { get; }
    public bool HasMultipleCandidates => CandidatePaths.Count > 1;
    public bool IsAvailable => CandidatePaths.Any(path =>
        path.Equals(SourcePath, StringComparison.OrdinalIgnoreCase));
    public bool IsAmbiguous => HasMultipleCandidates && !IsAvailable;

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            var selectedPath = CandidatePaths.FirstOrDefault(path =>
                path.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(value) && selectedPath is null)
            {
                return;
            }

            if (SetProperty(ref _sourcePath, selectedPath ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(IsAmbiguous));
                OnPropertyChanged(nameof(PathDisplay));
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public string PathDisplay => IsAmbiguous ? Strings.Mo2Service_SelectFolder : SourcePath;
    public string Status => IsOverwrite
        ? "overwrite"
        : IsAmbiguous
            ? LocalizedText.Format(Strings.Mo2Service_SelectOneOfFormat, CandidatePaths.Count)
            : HasMultipleCandidates
                ? Strings.Mo2Service_SelectedManually
            : IsAvailable
                ? Strings.Mo2Service_Found
                : Strings.Mo2Service_FolderMissing;
}

public sealed record Mo2FolderCandidate(string Path)
{
    public string Name => Path;
    public override string ToString() => Name;
}

public sealed record Mo2ImportPreview(
    Mo2ImportDiscovery Discovery,
    Mo2ProfileSource Profile,
    IReadOnlyList<Mo2ImportPreviewEntry> Entries,
    int SeparatorCount,
    bool HasOverwriteContent,
    int OverwriteFileCount,
    IReadOnlyList<string> OverwriteFileSamples,
    string ExecutableSummary)
{
    public int FoundModCount => Entries.Count(entry => entry.IsAvailable && !entry.IsOverwrite);
    public int EnabledModCount => Entries.Count(entry => entry.IsAvailable && entry.IsEnabled && !entry.IsOverwrite);
    public int MissingModCount => Entries.Count(entry => !entry.IsAvailable && !entry.IsAmbiguous);
    public int AmbiguousModCount => Entries.Count(entry => entry.IsAmbiguous);
    public string OverwriteSummary => HasOverwriteContent
        ? LocalizedText.Format(
            Strings.Mo2Service_OverwriteFormat,
            OverwriteFileCount,
            string.Join(", ", OverwriteFileSamples),
            OverwriteFileCount > OverwriteFileSamples.Count ? "…" : string.Empty)
        : Strings.Mo2Service_OverwriteEmpty;
}

public static class Mo2ImportService
{
    public static Mo2ImportDiscovery Discover(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new ArgumentException(Strings.Mo2Service_SelectSource, nameof(selectedPath));
        }

        var fullPath = Path.GetFullPath(selectedPath);
        var selectedModList = File.Exists(fullPath) ? fullPath : null;
        var selectedDirectory = selectedModList is null ? fullPath : Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(selectedDirectory))
        {
            throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Mo2Service_FolderNotFoundFormat, selectedDirectory));
        }

        if (selectedModList is not null &&
            !Path.GetFileName(selectedModList).Equals("modlist.txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Mo2Service_ModListRequired);
        }

        var selectedProfileDirectory = FindSelectedProfileDirectory(selectedDirectory, selectedModList);
        var rootPath = FindMo2Root(selectedDirectory, selectedProfileDirectory);
        var iniPath = FindIniPath(rootPath, selectedDirectory);
        var ini = iniPath is null ? new Dictionary<string, string>() : ReadIni(iniPath);
        var basePath = ResolveConfiguredPath(GetValue(ini, "base_directory"), rootPath, rootPath);
        var profilesPath = selectedProfileDirectory is not null
            ? Path.GetDirectoryName(selectedProfileDirectory)!
            : ResolveConfiguredPath(
                GetValue(ini, "profiles_directory", "profile_directory", @"paths\profiles"),
                basePath,
                Path.Combine(rootPath, "profiles"));
        var modsPath = ResolveConfiguredPath(
            GetValue(ini, "mods_directory", "mod_directory", "mods", @"paths\mods"),
            basePath,
            Path.Combine(rootPath, "mods"));
        var overwritePath = ResolveConfiguredPath(
            GetValue(ini, "overwrite_directory", "overwrite", @"paths\overwrite"),
            basePath,
            Path.Combine(rootPath, "overwrite"));
        var gamePath = ResolveConfiguredPath(
            GetValue(ini, "gamepath", "game_path", "game_directory"),
            basePath,
            string.Empty);

        var profiles = FindProfiles(profilesPath, selectedProfileDirectory, selectedModList);
        if (profiles.Count == 0)
        {
            throw new InvalidDataException(Strings.Mo2Service_NoProfiles);
        }

        var selectedProfileName = DecodeIniValue(GetValue(ini, "selected_profile"));
        var selectedProfile = profiles.FirstOrDefault(profile =>
                                  selectedProfileDirectory is not null &&
                                  FileSystemSafety.IsSameDirectory(profile.DirectoryPath, selectedProfileDirectory))
                              ?? profiles.FirstOrDefault(profile =>
                                  profile.Name.Equals(selectedProfileName, StringComparison.OrdinalIgnoreCase))
                              ?? profiles[0];

        return new Mo2ImportDiscovery(
            rootPath,
            profilesPath,
            modsPath,
            overwritePath,
            gamePath,
            profiles,
            selectedProfile);
    }

    public static Mo2ImportPreview CreatePreview(
        Mo2ImportDiscovery discovery,
        Mo2ProfileSource profile,
        string gamePath,
        string modsPath,
        string overwritePath)
    {
        if (!File.Exists(profile.ModListPath))
        {
            throw new FileNotFoundException(Strings.Mo2Service_ModListMissing, profile.ModListPath);
        }

        var modDirectories = ReadModDirectories(modsPath);
        var entriesInMo2Order = new List<PendingEntry>();
        var entriesWaitingForSeparator = new List<PendingEntry>();
        var separatorCount = 0;

        foreach (var entry in ParseModList(File.ReadLines(profile.ModListPath)))
        {
            var candidates = FindCandidates(entry.Name, modDirectories);
            if (IsSeparator(entry.Name, candidates))
            {
                var groupName = CleanSeparatorName(entry.Name);
                entriesInMo2Order.AddRange(entriesWaitingForSeparator.Select(
                    pending => pending with { GroupName = groupName }));
                entriesWaitingForSeparator.Clear();
                separatorCount++;
                continue;
            }

            entriesWaitingForSeparator.Add(new PendingEntry(entry.Name, entry.IsEnabled, string.Empty, candidates));
        }

        entriesInMo2Order.AddRange(entriesWaitingForSeparator);

        if (entriesInMo2Order.Count == 0)
        {
            throw new InvalidDataException(Strings.Mo2Service_NoMods);
        }

        var launcherEntries = entriesInMo2Order
            .AsEnumerable()
            .Reverse()
            .Select((entry, index) => new Mo2ImportPreviewEntry(
                index + 1,
                entry.Name,
                entry.Candidates.Count == 1 ? entry.Candidates[0] : string.Empty,
                entry.IsEnabled,
                entry.GroupName,
                false,
                entry.Candidates))
            .ToList();

        var overwriteContents = ReadOverwriteFiles(overwritePath);
        var hasOverwriteContent = overwriteContents.FileCount > 0;
        var executableSummary = DetectExecutableSummary(gamePath, launcherEntries);
        return new Mo2ImportPreview(
            discovery with
            {
                GamePath = NormalizeExistingPath(gamePath),
                ModsPath = NormalizeExistingPath(modsPath),
                OverwritePath = NormalizeOptionalPath(overwritePath),
                SelectedProfile = profile
            },
            profile,
            launcherEntries,
            separatorCount,
            hasOverwriteContent,
            overwriteContents.FileCount,
            overwriteContents.Samples,
            executableSummary);
    }

    public static ModProfile CreateProfile(Mo2ImportPreview preview, string requestedName, bool includeOverwrite)
    {
        if (!Directory.Exists(preview.Discovery.GamePath))
        {
            throw new DirectoryNotFoundException(Strings.Mo2Service_GameFolderMissing);
        }

        if (preview.AmbiguousModCount > 0)
        {
            throw new InvalidOperationException(Strings.Mo2Service_AmbiguousFolders);
        }

        var profile = new ModProfile
        {
            Name = string.IsNullOrWhiteSpace(requestedName) ? preview.Profile.Name : requestedName.Trim(),
            Description = LocalizedText.Format(Strings.Mo2Service_ImportedDescriptionFormat, preview.Profile.Name),
            GameInstallPath = Path.GetFullPath(preview.Discovery.GamePath),
            IsStandalone = false,
            LaunchBackendKind = LaunchBackendKind.LinkedWorkspace,
            LaunchArguments = string.Empty,
            Mo2OverwritePath = includeOverwrite && preview.HasOverwriteContent
                ? preview.Discovery.OverwritePath
                : string.Empty
        };

        foreach (var entry in preview.Entries.Where(entry => entry.IsAvailable && !entry.IsOverwrite))
        {
            profile.Mods.Add(new ModEntry
            {
                Name = entry.Name,
                SourcePath = entry.SourcePath,
                IsEnabled = entry.IsEnabled,
                GroupName = entry.GroupName,
                Order = profile.Mods.Count + 1
            });
        }

        var detection = DetectReliableExecutable(profile.GameInstallPath, profile.Mods);
        if (detection is not null)
        {
            profile.ExecutableRelativePath = detection.RelativePath;
        }

        return profile;
    }

    private static string? FindSelectedProfileDirectory(string selectedDirectory, string? selectedModList)
    {
        if (selectedModList is not null || File.Exists(Path.Combine(selectedDirectory, "modlist.txt")))
        {
            return selectedDirectory;
        }

        return null;
    }

    private static string FindMo2Root(string selectedDirectory, string? selectedProfileDirectory)
    {
        for (var current = new DirectoryInfo(selectedDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "ModOrganizer.ini")) ||
                Directory.Exists(Path.Combine(current.FullName, "profiles")) &&
                Directory.Exists(Path.Combine(current.FullName, "mods")))
            {
                return current.FullName;
            }
        }

        if (selectedProfileDirectory is not null)
        {
            var profilesDirectory = Directory.GetParent(selectedProfileDirectory);
            if (profilesDirectory?.Name.Equals("profiles", StringComparison.OrdinalIgnoreCase) == true &&
                profilesDirectory.Parent is not null)
            {
                return profilesDirectory.Parent.FullName;
            }
        }

        return selectedDirectory;
    }

    private static string? FindIniPath(string rootPath, string selectedDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(rootPath, "ModOrganizer.ini"),
            Path.Combine(selectedDirectory, "ModOrganizer.ini")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<Mo2ProfileSource> FindProfiles(
        string profilesPath,
        string? selectedProfileDirectory,
        string? selectedModList)
    {
        var profiles = new List<Mo2ProfileSource>();
        if (Directory.Exists(profilesPath))
        {
            foreach (var directory in Directory.EnumerateDirectories(profilesPath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var modList = Path.Combine(directory, "modlist.txt");
                if (File.Exists(modList))
                {
                    profiles.Add(new Mo2ProfileSource(Path.GetFileName(directory), directory, modList));
                }
            }
        }

        if (profiles.Count == 0 && selectedProfileDirectory is not null)
        {
            var modList = selectedModList ?? Path.Combine(selectedProfileDirectory, "modlist.txt");
            if (File.Exists(modList))
            {
                profiles.Add(new Mo2ProfileSource(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedProfileDirectory)),
                    selectedProfileDirectory,
                    modList));
            }
        }

        return profiles;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#' or '[')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string GetValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string DecodeIniValue(string value)
    {
        var decoded = value.Trim().Trim('"');
        const string byteArrayPrefix = "@ByteArray(";
        if (decoded.StartsWith(byteArrayPrefix, StringComparison.OrdinalIgnoreCase) && decoded.EndsWith(')'))
        {
            decoded = decoded[byteArrayPrefix.Length..^1];
        }

        return decoded.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string ResolveConfiguredPath(string value, string basePath, string fallback)
    {
        var decoded = Environment.ExpandEnvironmentVariables(DecodeIniValue(value));
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return string.IsNullOrWhiteSpace(fallback) ? string.Empty : Path.GetFullPath(fallback);
        }

        decoded = decoded
            .Replace("%BASE_DIR%", basePath, StringComparison.OrdinalIgnoreCase)
            .Replace("{BASE_DIR}", basePath, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(decoded) ? decoded : Path.Combine(basePath, decoded));
    }

    private static ModDirectory[] ReadModDirectories(string modsPath)
    {
        if (!Directory.Exists(modsPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(modsPath)
            .Select(path => new ModDirectory(
                Path.GetFullPath(path),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                ReadMetaName(path),
                IsMetaSeparator(path)))
            .ToArray();
    }

    private static string[] FindCandidates(string name, ModDirectory[] directories)
    {
        var exact = directories
            .Where(directory => directory.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(directory => directory.Path)
            .ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        var normalized = NormalizeName(name);
        return directories
            .Where(directory => NormalizeName(directory.FolderName) == normalized ||
                                NormalizeName(directory.MetaName) == normalized)
            .Select(directory => directory.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSeparator(string name, string[] candidates)
    {
        if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidates.Length == 1 && IsMetaSeparator(candidates[0]);
    }

    private static bool IsMetaSeparator(string directory)
    {
        var metaPath = Path.Combine(directory, "meta.ini");
        return File.Exists(metaPath) && File.ReadLines(metaPath).Any(line =>
            line.Trim().Equals("separator=true", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadMetaName(string directory)
    {
        var metaPath = Path.Combine(directory, "meta.ini");
        if (!File.Exists(metaPath))
        {
            return string.Empty;
        }

        foreach (var line in File.ReadLines(metaPath))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 && line[..separator].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeIniValue(line[(separator + 1)..]);
            }
        }

        return string.Empty;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string CleanSeparatorName(string name)
    {
        var value = name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_separator".Length]
            : name;
        return value.Trim(' ', '-', '_', '=');
    }

    private static List<ModListEntry> ParseModList(IEnumerable<string> lines)
    {
        var result = new List<ModListEntry>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length < 2 || line[0] == '#')
            {
                continue;
            }

            if (line[0] is not ('+' or '-' or '*'))
            {
                continue;
            }

            var name = line[1..].Trim();
            if (name.Length > 0)
            {
                result.Add(new ModListEntry(name, line[0] is '+' or '*'));
            }
        }

        return result;
    }

    private static OverwriteContents ReadOverwriteFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return new OverwriteContents(0, []);
        }

        var count = 0;
        var samples = new List<string>(8);
        foreach (var file in Directory.EnumerateFiles(
                     path,
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
        {
            count++;
            if (samples.Count < 8)
            {
                samples.Add(Path.GetRelativePath(path, file));
            }
        }

        samples.Sort(StringComparer.OrdinalIgnoreCase);
        return new OverwriteContents(count, samples);
    }

    private static string DetectExecutableSummary(
        string gamePath,
        IReadOnlyList<Mo2ImportPreviewEntry> entries)
    {
        var mods = entries.Where(entry => entry.IsAvailable && !entry.IsOverwrite)
            .Select((entry, index) => new ModEntry
            {
                Name = entry.Name,
                SourcePath = entry.SourcePath,
                IsEnabled = entry.IsEnabled,
                Order = index + 1
            })
            .ToArray();
        return DetectReliableExecutable(gamePath, mods)?.Summary ?? Strings.Mo2Service_ExecutableUncertain;
    }

    private static LaunchExecutableDetection? DetectReliableExecutable(string gamePath, IEnumerable<ModEntry> mods)
    {
        var detection = DetectExecutable(gamePath, mods);
        return detection is { Score: <= 45 } ? detection : null;
    }

    private static LaunchExecutableDetection? DetectExecutable(string gamePath, IEnumerable<ModEntry> mods)
    {
        var roots = new List<LaunchExecutableSearchRoot>();
        if (Directory.Exists(gamePath))
        {
            roots.Add(new LaunchExecutableSearchRoot(gamePath, Strings.Layer_BaseGame, 0, IsBaseGameRoot: true));
        }

        roots.AddRange(mods
            .Where(mod => mod.IsEnabled && Directory.Exists(mod.SourcePath))
            .OrderBy(mod => mod.Order)
            .Select(mod => new LaunchExecutableSearchRoot(mod.SourcePath, LocalizedText.Format(Strings.Layer_ModFormat, mod.Name), mod.Order)));
        return LaunchExecutableDetector.DetectBest(roots, requestedRelativePath: null);
    }

    private static string NormalizeExistingPath(string path) =>
        Directory.Exists(path) ? Path.GetFullPath(path) : path.Trim();

    private static string NormalizeOptionalPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private sealed record ModListEntry(string Name, bool IsEnabled);
    private sealed record PendingEntry(
        string Name,
        bool IsEnabled,
        string GroupName,
        IReadOnlyList<string> Candidates);
    private sealed record ModDirectory(string Path, string FolderName, string MetaName, bool IsSeparator);
    private sealed record OverwriteContents(int FileCount, IReadOnlyList<string> Samples);
}
