namespace StalkerModLauncher.Models;

public sealed record OverlayLayerSnapshot(
    FileLayerKind Kind,
    string Id,
    string Name,
    string RootPath,
    int Order);

public sealed record OverlayExecutableSnapshot(
    string FullPath,
    string RelativePath,
    string SourceName,
    bool IsPinned,
    bool UsesRequestedPath);

public sealed record OverlayWritableFileSnapshot(
    string RelativePath,
    string StoragePath,
    string Reason);

public sealed record OverlaySystemFileSnapshot(
    string RelativePath,
    FileLayerFile? Source);

public sealed record OverlayManifest(
    IReadOnlyList<OverlayLayerSnapshot> Layers,
    OverlayExecutableSnapshot? Executable,
    LaunchPlan? LaunchPlan,
    IReadOnlyList<OverlaySystemFileSnapshot> SystemFiles,
    string WriteOverlayRoot,
    IReadOnlyList<OverlayWritableFileSnapshot> WritableFiles,
    IReadOnlyList<FileLayerOverwrite> Overwrites)
{
    public bool UsesMiseryClasses { get; init; }
}
