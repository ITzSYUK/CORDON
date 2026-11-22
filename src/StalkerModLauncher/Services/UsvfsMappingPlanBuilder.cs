using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class UsvfsMappingPlanBuilder
{
    public static UsvfsMappingPlan Build(
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string? virtualRootOverride = null)
    {
        var baseGameRoot = Path.GetFullPath(layerPlan.BaseGame.RootPath);
        var virtualRoot = Path.GetFullPath(virtualRootOverride ?? baseGameRoot);
        var operations = new List<UsvfsMappingOperation>();
        var dataTargets = GetDataTargets(layerPlan, virtualRoot, includeAppDataAlias: false);

        // Never map the game directory onto itself: USVFS would hide physical files
        // such as gamedata.db*. A separate bootstrap root does need the base layer.
        if (!FileSystemSafety.IsSameDirectory(baseGameRoot, virtualRoot))
        {
            AddLayerDirectory(operations, layerPlan.BaseGame, baseGameRoot, virtualRoot, dataTargets);
        }

        foreach (var layer in layerPlan.Mods)
        {
            AddLayerDirectory(operations, layer, Path.GetFullPath(layer.RootPath), virtualRoot, dataTargets);
        }

        AddExcludedFileRestorations(operations, layerPlan, virtualRoot);
        AddWritableFiles(operations, virtualRoot, manifest);

        AddWriteTargets(operations, layerPlan, manifest, virtualRoot, dataTargets);
        AddMissingParentDirectories(operations, manifest, virtualRoot);

        return new UsvfsMappingPlan(
            virtualRoot,
            Path.GetFullPath(manifest.WriteOverlayRoot),
            operations);
    }

    public static UsvfsMappingPlan BuildAnomalyLauncherBootstrap(
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string virtualRoot)
    {
        var fullVirtualRoot = Path.GetFullPath(virtualRoot);
        var operations = new List<UsvfsMappingOperation>();
        var dataTargets = GetDataTargets(layerPlan, fullVirtualRoot, includeAppDataAlias: true);

        AddLayerDirectoriesAroundPhysicalBin(
            operations,
            layerPlan.BaseGame,
            fullVirtualRoot, dataTargets);
        foreach (var layer in layerPlan.Mods)
        {
            AddLayerDirectoriesAroundPhysicalBin(
                operations,
                layer,
                fullVirtualRoot, dataTargets);
        }

        AddExcludedFileRestorations(operations, layerPlan, fullVirtualRoot);
        AddWritableFiles(operations, fullVirtualRoot, manifest);
        AddWriteTargets(operations, layerPlan, manifest, fullVirtualRoot, dataTargets);
        AddMissingParentDirectories(operations, manifest, fullVirtualRoot);

        return new UsvfsMappingPlan(
            fullVirtualRoot,
            Path.GetFullPath(manifest.WriteOverlayRoot),
            operations);
    }

    private static void AddLayerDirectoriesAroundPhysicalBin(
        List<UsvfsMappingOperation> operations,
        FileLayer layer,
        string virtualRoot,
        string[] dataTargets)
    {
        var sourceRoot = Path.GetFullPath(layer.RootPath);
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var directoryName = Path.GetFileName(sourceDirectory);
            if (!directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                AddLayerDirectory(operations, layer, sourceDirectory, Path.Combine(virtualRoot, directoryName), dataTargets);
                continue;
            }

            foreach (var binSubdirectory in Directory.EnumerateDirectories(
                         sourceDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                AddLayerDirectory(operations, layer, binSubdirectory,
                    Path.Combine(virtualRoot, "bin", Path.GetFileName(binSubdirectory)), dataTargets);
            }
        }
    }

    private static void AddLayerDirectory(
        List<UsvfsMappingOperation> operations, FileLayer layer, string sourceDirectory, string destinationDirectory,
        string[] dataTargets)
    {
        if (dataTargets.Any(target => FileSystemSafety.IsDirectoryInside(destinationDirectory, target))) return;
        var exclusions = (layer.Mod?.ExcludedFiles ?? [])
            .Select(path => FileSystemSafety.ResolvePathInside(layer.RootPath, path, Strings.Safety_ExcludedModFile))
            .Where(path => FileSystemSafety.IsDirectoryInside(path, sourceDirectory))
            .ToArray();
        if (exclusions.Length == 0 && !dataTargets.Any(target => FileSystemSafety.IsDirectoryInside(target, destinationDirectory)))
        {
            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.DirectoryStatic, sourceDirectory, destinationDirectory,
                FileLayerPlan.GetDisplayName(layer), layer.Order, MonitorChanges: false, CreateTarget: false));
            return;
        }

        // Split only branches containing exclusions; a recursive mapping would expose excluded files.
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            if (exclusions.Contains(entry, StringComparer.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                AddLayerDirectory(operations, layer, entry, destination, dataTargets);
            else
                operations.Add(new UsvfsMappingOperation(
                    UsvfsMappingKind.File, entry, destination,
                    FileLayerPlan.GetDisplayName(layer), layer.Order, MonitorChanges: false, CreateTarget: false));
        }
    }

    private static void AddMissingParentDirectories(
        List<UsvfsMappingOperation> operations, OverlayManifest manifest, string virtualRoot)
    {
        var ordered = operations.OrderBy(operation => operation.Order).ToArray();
        operations.Clear();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { virtualRoot };
        string? emptyDirectory = null;
        foreach (var operation in ordered)
        {
            var missing = new Stack<string>();
            for (var parent = Path.GetDirectoryName(operation.DestinationPath);
                 parent is not null && FileSystemSafety.IsDirectoryInside(parent, virtualRoot) &&
                 !directories.Contains(parent);
                 parent = Path.GetDirectoryName(parent))
                missing.Push(parent);
            while (missing.TryPop(out var parent))
            {
                // Physical parents can be hidden by a sparse mod mapping. LinkFile
                // needs mapped parents even when those directories exist on disk.
                emptyDirectory ??= Path.GetFullPath(Path.Combine(manifest.WriteOverlayRoot, "..", "..", ".usvfs-empty-directory"));
                FileSystemSafety.EnsureNoDirectoryLinks(emptyDirectory);
                Directory.CreateDirectory(emptyDirectory);
                if (Directory.EnumerateFileSystemEntries(emptyDirectory).Any())
                    throw new InvalidOperationException(LocalizedText.Format(Strings.Error_WorkspaceUnverifiedFormat, emptyDirectory));
                operations.Add(new UsvfsMappingOperation(UsvfsMappingKind.DirectoryStatic,
                    emptyDirectory, parent, operation.SourceName, operation.Order));
                directories.Add(parent);
            }
            operations.Add(operation);
            if (operation.Kind == UsvfsMappingKind.DirectoryStatic) directories.Add(operation.DestinationPath);
        }
    }

    internal static IReadOnlyList<UsvfsMappingOperation> EnsureMappedParents(
        IEnumerable<UsvfsMappingOperation> operations, OverlayManifest manifest, string virtualRoot)
    {
        var result = operations.ToList();
        AddMissingParentDirectories(result, manifest, virtualRoot);
        return result;
    }

    private static void AddWritableFiles(
        List<UsvfsMappingOperation> operations,
        string virtualRoot,
        OverlayManifest manifest)
    {
        // Only active paths belong in this launch. Mapping the entire store would
        // expose old class copies even after MISERY or a class template is disabled.
        foreach (var writableFile in manifest.WritableFiles)
        {
            if (!File.Exists(writableFile.StoragePath))
                continue;
            FileSystemSafety.EnsureRelativePath(writableFile.RelativePath, Strings.Safety_UsvfsWritableFile);
            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.File,
                Path.GetFullPath(writableFile.StoragePath),
                Path.Combine(virtualRoot, writableFile.RelativePath),
                Strings.Layer_ProfileWritableData,
                int.MaxValue - 1,
                MonitorChanges: false,
                CreateTarget: false));
        }
    }

    private static string[] GetDataTargets(FileLayerPlan layerPlan, string virtualRoot, bool includeAppDataAlias)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FileSystemSafety.IsDirectoryInside(layerPlan.GameDataRoot, virtualRoot))
            targets.Add(Path.TrimEndingDirectorySeparator(layerPlan.GameDataRoot));
        if (includeAppDataAlias && layerPlan.UsesAnomalyAppDataAlias)
            targets.Add(Path.Combine(virtualRoot, "appdata"));
        return targets.ToArray();
    }

    private static void AddWriteTargets(
        List<UsvfsMappingOperation> operations,
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string virtualRoot,
        string[] dataTargets)
    {
        var overwriteRoot = Path.GetFullPath(manifest.WriteOverlayRoot);
        var split = dataTargets.Length > 0;
        operations.Add(new UsvfsMappingOperation(UsvfsMappingKind.DirectoryStatic,
            overwriteRoot, virtualRoot, Strings.Layer_ProfileOverwrite, int.MaxValue,
            MonitorChanges: !split, CreateTarget: true, Recursive: !split));
        if (!split) return;

        AddOverwriteChildren(overwriteRoot, virtualRoot);
        operations.RemoveAll(operation => dataTargets.Any(target =>
            FileSystemSafety.IsDirectoryInside(operation.DestinationPath, target)));
        foreach (var destination in dataTargets)
            operations.Add(new UsvfsMappingOperation(UsvfsMappingKind.DirectoryStatic,
                Path.TrimEndingDirectorySeparator(layerPlan.GameDataRoot), destination, Strings.Layer_ProfileWritableData, int.MaxValue,
                CreateTarget: true, Recursive: false));

        void AddOverwriteChildren(string source, string destination)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var targetPath = Path.Combine(destination, Path.GetFileName(entry));
                if (dataTargets.Any(target => FileSystemSafety.IsDirectoryInside(targetPath, target))) continue;
                var directory = Directory.Exists(entry);
                if (directory && dataTargets.Any(target => FileSystemSafety.IsDirectoryInside(target, targetPath)))
                {
                    FileSystemSafety.EnsureNoDirectoryLinks(entry);
                    AddOverwriteChildren(entry, targetPath);
                    continue;
                }

                operations.Add(new UsvfsMappingOperation(
                    directory ? UsvfsMappingKind.DirectoryStatic : UsvfsMappingKind.File,
                    entry, targetPath, Strings.Layer_ProfileOverwrite, int.MaxValue));
            }
        }
    }

    private static void AddExcludedFileRestorations(
        List<UsvfsMappingOperation> operations,
        FileLayerPlan layerPlan,
        string virtualRoot)
    {
        var excludedPaths = layerPlan.Mods
            .SelectMany(layer => layer.Mod?.ExcludedFiles ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in excludedPaths)
        {
            FileSystemSafety.EnsureRelativePath(relativePath, Strings.Safety_ExcludedModFile);
            var provider = layerPlan.FindFinalFile(relativePath);
            if (provider is null)
            {
                continue;
            }

            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.File,
                Path.GetFullPath(provider.FullPath),
                Path.Combine(virtualRoot, relativePath),
                LocalizedText.Format(Strings.Layer_ExcludedFallbackFormat, provider.SourceName),
                int.MaxValue - 2,
                MonitorChanges: false,
                CreateTarget: false));
        }
    }
}
