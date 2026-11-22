namespace StalkerModLauncher.Models;

public enum UsvfsMappingKind
{
    DirectoryStatic,
    File
}

public sealed record UsvfsMappingOperation(
    UsvfsMappingKind Kind,
    string SourcePath,
    string DestinationPath,
    string SourceName,
    int Order,
    bool MonitorChanges = false,
    bool CreateTarget = false,
    bool Recursive = true);

public sealed record UsvfsMappingPlan(
    string VirtualRoot,
    string WriteOverlayRoot,
    IReadOnlyList<UsvfsMappingOperation> Operations);
