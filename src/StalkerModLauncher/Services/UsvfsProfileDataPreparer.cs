using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

internal static class UsvfsProfileDataPreparer
{
    public static string? Prepare(
        FileLayerPlan layerPlan,
        OverlayManifest manifest,
        string profileWorkspace,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = layerPlan.FindFinalFile("fsgame.ltx");
        if (source is null)
        {
            throw new FileNotFoundException(
                "fsgame.ltx was not found in the enabled layers. " +
                "Profile-local saves and logs cannot be guaranteed.");
        }

        var profileDataPath = layerPlan.GameDataRoot;
        var destination = Path.Combine(manifest.WriteOverlayRoot, "fsgame.ltx");
        ProfileWritableGameFileStore.PrepareForVirtualFileSystem(
            layerPlan,
            profileWorkspace,
            progress);
        ProfileDataConfigurator.WriteProfileFsgame(source.FullPath, destination, profileDataPath);
        Directory.CreateDirectory(layerPlan.GameDataRoot);
        if (layerPlan.UsesSharedGameData)
        {
            progress?.Report($"USVFS: общие данные базовой игры: {layerPlan.GameDataRoot}. Существующие файлы сохранены.");
            return destination;
        }
        ProfileDataConfigurator.EnsureProfileUserLtx(
            layerPlan,
            profileDataPath,
            progress);
        ProfileShaderCacheSeeder.Seed(layerPlan, profileDataPath, progress, cancellationToken);
        progress?.Report($"USVFS profile fsgame.ltx prepared from {source.SourceName}.");
        return destination;
    }
}
