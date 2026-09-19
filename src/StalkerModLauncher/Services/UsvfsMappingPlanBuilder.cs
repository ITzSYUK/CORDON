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

        // Never map the game directory onto itself: USVFS would hide physical files
        // such as gamedata.db*. A separate bootstrap root does need the base layer.
        if (!FileSystemSafety.IsSameDirectory(baseGameRoot, virtualRoot))
        {
            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.DirectoryStatic,
                baseGameRoot,
                virtualRoot,
                FileLayerPlan.GetDisplayName(layerPlan.BaseGame),
                layerPlan.BaseGame.Order,
                MonitorChanges: false,
                CreateTarget: false));
        }

        foreach (var layer in layerPlan.Mods)
        {
            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.DirectoryStatic,
                Path.GetFullPath(layer.RootPath),
                virtualRoot,
                FileLayerPlan.GetDisplayName(layer),
                layer.Order,
                MonitorChanges: false,
                CreateTarget: false));
        }

        AddExcludedFileRestorations(operations, layerPlan, virtualRoot);
        AddWritableFiles(operations, virtualRoot, manifest);

        AddProfileOverwrite(operations, layerPlan, manifest, virtualRoot);

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

        AddLayerDirectoriesAroundPhysicalBin(
            operations,
            layerPlan.BaseGame,
            fullVirtualRoot);
        foreach (var layer in layerPlan.Mods)
        {
            AddLayerDirectoriesAroundPhysicalBin(
                operations,
                layer,
                fullVirtualRoot);
        }

        AddExcludedFileRestorations(operations, layerPlan, fullVirtualRoot);
        AddWritableFiles(operations, fullVirtualRoot, manifest);
        AddProfileOverwrite(operations, layerPlan, manifest, fullVirtualRoot);

        return new UsvfsMappingPlan(
            fullVirtualRoot,
            Path.GetFullPath(manifest.WriteOverlayRoot),
            operations);
    }

    private static void AddLayerDirectoriesAroundPhysicalBin(
        List<UsvfsMappingOperation> operations,
        FileLayer layer,
        string virtualRoot)
    {
        var sourceRoot = Path.GetFullPath(layer.RootPath);
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var sourceName = FileLayerPlan.GetDisplayName(layer);
        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var directoryName = Path.GetFileName(sourceDirectory);
            if (!directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    sourceDirectory,
                    Path.Combine(virtualRoot, directoryName),
                    sourceName,
                    layer.Order,
                    MonitorChanges: false,
                    CreateTarget: false));
                continue;
            }

            foreach (var binSubdirectory in Directory.EnumerateDirectories(
                         sourceDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                operations.Add(new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    binSubdirectory,
                    Path.Combine(virtualRoot, "bin", Path.GetFileName(binSubdirectory)),
                    sourceName,
                    layer.Order,
                    MonitorChanges: false,
                    CreateTarget: false));
            }
        }
    }

    private static void AddWritableFiles(
        List<UsvfsMappingOperation> operations,
        string virtualRoot,
        OverlayManifest manifest)
    {
        var writableFiles = manifest.WritableFiles
            .Where(file => File.Exists(file.StoragePath))
            .ToArray();
        if (writableFiles.Length == 0)
        {
            return;
        }

        foreach (var writableFile in writableFiles)
        {
            FileSystemSafety.EnsureRelativePath(writableFile.RelativePath, Strings.Safety_UsvfsWritableFile);
        }

        var profileWorkspace = Path.GetFullPath(Path.Combine(manifest.WriteOverlayRoot, "..", ".."));
        var writableRoot = Path.Combine(
            profileWorkspace,
            ProfileWritableGameFiles.WritableGameFilesRootRelativePath);
        operations.Add(new UsvfsMappingOperation(
            UsvfsMappingKind.DirectoryStatic,
            writableRoot,
            virtualRoot,
            Strings.Layer_ProfileWritableData,
            int.MaxValue - 1,
            MonitorChanges: false,
            CreateTarget: false));
    }

    private static void AddProfileOverwrite(
        List<UsvfsMappingOperation> operations,
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string virtualRoot)
    {
        var overwriteRoot = Path.GetFullPath(manifest.WriteOverlayRoot);
        if (!layerPlan.UsesSharedGameData)
        {
            operations.Add(new UsvfsMappingOperation(
                UsvfsMappingKind.DirectoryStatic,
                overwriteRoot,
                virtualRoot,
                Strings.Layer_ProfileOverwrite,
                int.MaxValue,
                MonitorChanges: true,
                CreateTarget: true));
            return;
        }

        // In shared-data mode userdata belongs to the game installation. Mapping it
        // from the profile overlay makes USVFS silently capture saves and logs again.
        foreach (var entry in Directory.EnumerateFileSystemEntries(overwriteRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(entry).Equals("userdata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            operations.Add(new UsvfsMappingOperation(
                Directory.Exists(entry) ? UsvfsMappingKind.DirectoryStatic : UsvfsMappingKind.File,
                entry,
                Path.Combine(virtualRoot, Path.GetFileName(entry)),
                Strings.Layer_ProfileOverwrite,
                int.MaxValue,
                MonitorChanges: true,
                CreateTarget: true));
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
