using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public sealed class ModConflictAnalyzer
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private const int MaxCacheEntries = 128;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, FileListCacheEntry> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    internal void ClearCache()
    {
        lock (_cacheSync)
        {
            _fileCache.Clear();
        }
    }

    public Task<IReadOnlyDictionary<string, ModConflictState>> AnalyzeAsync(
        IReadOnlyList<ModConflictInput> mods,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeAsync(mods, null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, ModConflictState>> AnalyzeAsync(
        FileLayerPlan plan,
        string? launchExecutableRelativePath,
        string? launchExecutableSourcePath,
        CancellationToken cancellationToken = default)
    {
        var mods = plan.Mods
            .Select(ModConflictInput.FromLayer)
            .ToArray();
        return AnalyzeAsync(mods, launchExecutableRelativePath, launchExecutableSourcePath, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, ModConflictState>> AnalyzeAsync(
        IReadOnlyList<ModConflictInput> mods,
        string? launchExecutableRelativePath,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeAsync(mods, launchExecutableRelativePath, launchExecutableSourcePath: null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, ModConflictState>> AnalyzeAsync(
        IReadOnlyList<ModConflictInput> mods,
        string? launchExecutableRelativePath,
        string? launchExecutableSourcePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyDictionary<string, ModConflictState>>(
            () => Analyze(mods, launchExecutableRelativePath, launchExecutableSourcePath, cancellationToken),
            cancellationToken);
    }

    private Dictionary<string, ModConflictState> Analyze(
        IReadOnlyList<ModConflictInput> mods,
        string? launchExecutableRelativePath,
        string? launchExecutableSourcePath,
        CancellationToken cancellationToken)
    {
        var fileCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(mod => mod.IsEnabled && !string.IsNullOrWhiteSpace(mod.SourcePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = new HashSet<string>(
                GetModFileList(mod.SourcePath, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
            files.ExceptWith(mod.ExcludedFiles.Select(NormalizeRelativePath));
            fileCache[mod.Id] = files;
        }

        var normalizedExecutable = NormalizeRelativePath(launchExecutableRelativePath);
        var executableProviderId = FindPinnedExecutableProvider(mods, launchExecutableSourcePath, normalizedExecutable, fileCache)
            ?? mods
                .Where(mod => mod.IsEnabled)
                .LastOrDefault(mod => fileCache.GetValueOrDefault(mod.Id)?.Contains(normalizedExecutable) == true)
                ?.Id;

        var providersByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < mods.Count; index++)
        {
            if (!mods[index].IsEnabled || !fileCache.TryGetValue(mods[index].Id, out var files))
            {
                continue;
            }

            foreach (var relativePath in files)
            {
                if (!providersByPath.TryGetValue(relativePath, out var providers))
                {
                    providers = [];
                    providersByPath.Add(relativePath, providers);
                }

                providers.Add(index);
            }
        }

        var result = new Dictionary<string, ModConflictState>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < mods.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentFiles = fileCache.GetValueOrDefault(mods[index].Id);
            if (!mods[index].IsEnabled || currentFiles is null)
            {
                result[mods[index].Id] = ModConflictState.Disabled;
                continue;
            }

            var conflicts = new List<ModConflictFileState>();
            foreach (var relativePath in currentFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var providers = providersByPath[relativePath];
                var lower = providers.Where(provider => provider < index).ToArray();
                var higher = providers.Where(provider => provider > index).ToArray();
                if (lower.Length == 0 && higher.Length == 0)
                {
                    continue;
                }

                var finalProvider = providers[^1];
                conflicts.Add(new ModConflictFileState(
                    relativePath,
                    lower.Select(provider => mods[provider].Id).ToArray(),
                    lower.Select(provider => mods[provider].Name).ToArray(),
                    higher.Select(provider => mods[provider].Id).ToArray(),
                    higher.Select(provider => mods[provider].Name).ToArray(),
                    mods[finalProvider].Id,
                    mods[finalProvider].Name));
            }

            var overwrittenFiles = conflicts.Where(file => file.LowerPriorityModIds.Count > 0).ToArray();
            var overwrittenByFiles = conflicts.Where(file => file.HigherPriorityModIds.Count > 0).ToArray();
            var overwrittenModNames = DistinctProviderNames(overwrittenFiles.SelectMany(file => file.LowerPriorityModNames));
            var overwrittenByModNames = DistinctProviderNames(overwrittenByFiles.SelectMany(file => file.HigherPriorityModNames));
            var relatedIds = conflicts
                .SelectMany(file => file.LowerPriorityModIds.Concat(file.HigherPriorityModIds))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var isRedundant = currentFiles.Count > 0 && currentFiles.All(path => providersByPath[path][^1] > index);
            var conflictKind = isRedundant
                ? ModConflictKind.Redundant
                : (overwrittenFiles.Length > 0, overwrittenByFiles.Length > 0) switch
                {
                    (true, true) => ModConflictKind.Mixed,
                    (true, false) => ModConflictKind.Overwrite,
                    (false, true) => ModConflictKind.Overwritten,
                    _ => ModConflictKind.None
                };

            result[mods[index].Id] = new ModConflictState(
                conflictKind,
                overwrittenFiles.Length > 0,
                overwrittenFiles.Length,
                overwrittenModNames,
                overwrittenByFiles.Length,
                overwrittenByModNames,
                relatedIds,
                conflicts,
                string.Equals(mods[index].Id, executableProviderId, StringComparison.OrdinalIgnoreCase),
                overwrittenFiles.Count(file => IsConfigurationFile(file.RelativePath)),
                overwrittenFiles.Count(file => IsBinaryFile(file.RelativePath)),
                overwrittenByFiles.Count(file => IsConfigurationFile(file.RelativePath)),
                overwrittenByFiles.Count(file => IsBinaryFile(file.RelativePath)));
        }

        return result;
    }

    private static string[] DistinctProviderNames(IEnumerable<string> names)
    {
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindPinnedExecutableProvider(
        IReadOnlyList<ModConflictInput> mods,
        string? launchExecutableSourcePath,
        string normalizedExecutable,
        IReadOnlyDictionary<string, HashSet<string>> fileCache)
    {
        if (string.IsNullOrWhiteSpace(launchExecutableSourcePath))
        {
            return null;
        }

        var pinnedRoot = Path.GetFullPath(launchExecutableSourcePath);
        return mods
            .Where(mod => mod.IsEnabled && Directory.Exists(mod.SourcePath))
            .FirstOrDefault(mod =>
                FileSystemSafety.IsSameDirectory(mod.SourcePath, pinnedRoot) &&
                fileCache.GetValueOrDefault(mod.Id)?.Contains(normalizedExecutable) == true)
            ?.Id;
    }

    private static bool IsConfigurationFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".ltx" or ".xml" or ".ini" or ".cfg" or ".script";
    }

    private static bool IsBinaryFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll";
    }

    private HashSet<string> GetModFileList(string modPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var fullPath = Path.GetFullPath(modPath);
        var rootWriteTime = Directory.GetLastWriteTimeUtc(fullPath);
        lock (_cacheSync)
        {
            if (_fileCache.TryGetValue(fullPath, out var cached) &&
                cached.RootWriteTimeUtc == rootWriteTime &&
                DateTime.UtcNow - cached.CreatedAtUtc < CacheLifetime)
            {
                return cached.Files;
            }
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, "*", SafeEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(NormalizeRelativePath(Path.GetRelativePath(fullPath, file)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An inaccessible mod is handled by profile validation; conflict analysis stays best-effort.
        }

        lock (_cacheSync)
        {
            _fileCache[fullPath] = new FileListCacheEntry(files, rootWriteTime, DateTime.UtcNow);
            if (_fileCache.Count > MaxCacheEntries)
            {
                var oldest = _fileCache.MinBy(pair => pair.Value.CreatedAtUtc).Key;
                _fileCache.Remove(oldest);
            }
        }

        return files;
    }

    private static string NormalizeRelativePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private sealed record FileListCacheEntry(
        HashSet<string> Files,
        DateTime RootWriteTimeUtc,
        DateTime CreatedAtUtc);
}

public sealed record ModConflictInput(
    string Id,
    string Name,
    string SourcePath,
    bool IsEnabled,
    IReadOnlyList<string> ExcludedFiles)
{
    public static ModConflictInput FromMod(ModEntry mod)
    {
        return new ModConflictInput(mod.Id, mod.Name, mod.SourcePath, mod.IsEnabled, mod.ExcludedFiles);
    }

    public static ModConflictInput FromLayer(FileLayer layer)
    {
        return new ModConflictInput(
            layer.Id,
            layer.Name,
            layer.RootPath,
            IsEnabled: true,
            layer.Mod?.ExcludedFiles ?? []);
    }
}

public sealed record ModConflictState(
    ModConflictKind ConflictKind,
    bool HasOverlapsAbove,
    int OverwrittenFileCount,
    IReadOnlyList<string> OverwrittenModNames,
    int OverwrittenByFileCount,
    IReadOnlyList<string> OverwrittenByModNames,
    IReadOnlyList<string> RelatedModIds,
    IReadOnlyList<ModConflictFileState> Files,
    bool ProvidesLaunchExecutable,
    int OverwrittenConfigurationCount,
    int OverwrittenBinaryCount,
    int OverwrittenByConfigurationCount,
    int OverwrittenByBinaryCount)
{
    public static ModConflictState Disabled { get; } = new(
        ModConflictKind.Disabled,
        false,
        0,
        [],
        0,
        [],
        [],
        [],
        false,
        0,
        0,
        0,
        0);
}

public sealed record ModConflictFileState(
    string RelativePath,
    IReadOnlyList<string> LowerPriorityModIds,
    IReadOnlyList<string> LowerPriorityModNames,
    IReadOnlyList<string> HigherPriorityModIds,
    IReadOnlyList<string> HigherPriorityModNames,
    string FinalProviderId,
    string FinalProviderName);
