using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class FileLayerExplorerService
{
    public static Task<IReadOnlyList<FinalFileEntry>> BuildFinalTreeAsync(
        FileLayerPlan plan,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FinalFileEntry>>(
            () => BuildFinalTree(plan, workspacePath, cancellationToken),
            cancellationToken);
    }

    private static FinalFileEntry[] BuildFinalTree(
        FileLayerPlan plan,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        var providers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in plan.SourceLayers.Where(layer => Directory.Exists(layer.RootPath)))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(layer.RootPath, "*", SafeEnumerationOptions);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(layer.RootPath, file);
                if (FileLayerPlan.IsExcluded(layer, relativePath))
                {
                    continue;
                }

                AddProvider(providers, relativePath, FileLayerPlan.GetDisplayName(layer));
            }
        }

        AddProfileFiles(providers, workspacePath, cancellationToken);

        return providers
            .Select(pair => new FinalFileEntry(
                pair.Key,
                pair.Value[^1],
                pair.Value,
                pair.Value.Count > 1,
                IsBinary(pair.Key),
                IsConfiguration(pair.Key)))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddProfileFiles(
        IDictionary<string, List<string>> providers,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        var workspace = Path.GetFullPath(workspacePath);
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var storagePath = Path.Combine(workspace, rule.StorageRelativePath);
            if (File.Exists(storagePath))
            {
                AddProvider(providers, rule.RelativePath, Strings.Layer_ProfileWritableData);
            }
        }

        var overwriteRoot = Path.Combine(workspace, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);
        if (!Directory.Exists(overwriteRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(overwriteRoot, "*", SafeEnumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddProvider(providers, Path.GetRelativePath(overwriteRoot, file), Strings.Layer_ProfileOverwrite);
        }
    }

    private static void AddProvider(
        IDictionary<string, List<string>> providers,
        string relativePath,
        string provider)
    {
        if (!providers.TryGetValue(relativePath, out var fileProviders))
        {
            fileProviders = [];
            providers.Add(relativePath, fileProviders);
        }

        fileProviders.Add(provider);
    }

    private static bool IsBinary(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll";

    private static bool IsConfiguration(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ltx" or ".xml" or ".ini" or ".cfg" or ".script";

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };
}

public sealed record FinalFileEntry(
    string RelativePath,
    string FinalProvider,
    IReadOnlyList<string> Providers,
    bool HasConflict,
    bool IsBinary,
    bool IsConfiguration)
{
    public string ProvidersDisplay => string.Join(" → ", Providers);
}
