using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public sealed record ProfileLaunchBackendContext(
    string GamePath,
    ModProfile Profile,
    FileLayerPlan? FileLayerPlan = null,
    OverlayManifest? OverlayManifest = null,
    string? SavedGamePath = null);
