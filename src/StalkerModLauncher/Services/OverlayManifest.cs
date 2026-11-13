using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class OverlayManifestBuilder
{
    private static readonly string[] ImportantSystemFiles =
    [
        "fsgame.ltx",
        "user.ltx",
        Path.Combine("gamedata", "configs", "localization.ltx"),
        Path.Combine("gamedata", "configs", "axr_options.ltx")
    ];

    public static OverlayManifest BuildLinkedWorkspace(
        ModProfile profile,
        FileLayerPlan layerPlan,
        string profileWorkspace,
        bool includeOverwrites = false,
        CancellationToken cancellationToken = default)
    {
        var launch = ProfileLaunchPlanResolver.PreviewLinkedWorkspace(profile, layerPlan, profileWorkspace);
        return Build(
            layerPlan,
            profileWorkspace,
            launch,
            includeOverwrites,
            cancellationToken);
    }

    public static OverlayManifest BuildVirtualFileSystem(
        ModProfile profile,
        FileLayerPlan layerPlan,
        string profileWorkspace,
        bool includeOverwrites = false,
        CancellationToken cancellationToken = default)
    {
        var launch = ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, layerPlan);
        return Build(
            layerPlan,
            profileWorkspace,
            launch,
            includeOverwrites,
            cancellationToken);
    }

    private static OverlayManifest Build(
        FileLayerPlan layerPlan,
        string profileWorkspace,
        LaunchPlanResolution launch,
        bool includeOverwrites,
        CancellationToken cancellationToken)
    {
        var executable = launch.Executable is null
            ? null
            : new OverlayExecutableSnapshot(
                launch.Executable.FullPath,
                launch.Executable.RelativePath,
                launch.Executable.SourceName,
                launch.Executable.IsPinned,
                launch.Executable.UsedRequestedRelativePath);

        var miseryPaths = MiseryClassSupport.FindWritablePaths(layerPlan, cancellationToken);
        var writableFiles = ProfileWritableGameFiles.Rules
            .Select(rule => new OverlayWritableFileSnapshot(
                rule.RelativePath,
                Path.Combine(profileWorkspace, rule.StorageRelativePath),
                rule.Reason))
            .ToList();
        var knownPaths = writableFiles.Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in miseryPaths)
        {
            if (knownPaths.Add(relativePath))
                writableFiles.Add(new OverlayWritableFileSnapshot(
                    relativePath,
                    ProfileWritableGameFileStore.GetStoredFilePath(profileWorkspace, relativePath),
                    Strings.Writable_MiseryClassReason));
        }

        return new OverlayManifest(
            layerPlan.Layers
                .Select(layer => new OverlayLayerSnapshot(
                    layer.Kind,
                    layer.Id,
                    layer.Name,
                    layer.RootPath,
                    layer.Order))
                .ToArray(),
            executable,
            launch.Plan,
            ImportantSystemFiles
                .Select(relativePath => new OverlaySystemFileSnapshot(
                    relativePath,
                    layerPlan.FindFinalFile(relativePath)))
                .ToArray(),
            Path.Combine(profileWorkspace, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath),
            writableFiles,
            includeOverwrites
                ? layerPlan.Mods
                    .SelectMany(layer => layerPlan.GetOverwrittenFiles(layer, cancellationToken))
                    .ToArray()
                : [])
        {
            UsesMiseryClasses = miseryPaths.Count > 0
        };
    }
}
