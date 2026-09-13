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
        var source = layerPlan.FindFsgameSource();
        if (source is null)
        {
            throw new FileNotFoundException(ProfileDataConfigurator.MissingFsgameMessage);
        }

        var profileDataPath = layerPlan.GameDataRoot;
        var destination = FileSystemSafety.ResolvePathInside(
            manifest.WriteOverlayRoot,
            source.RelativePath,
            "Profile fsgame.ltx");
        ProfileDataConfigurator.MigrateLegacyManualData(layerPlan, manifest.WriteOverlayRoot, progress);
        ProfileWritableGameFileStore.PrepareForVirtualFileSystem(
            layerPlan,
            profileWorkspace,
            progress);
        ProfileDataConfigurator.WriteProfileFsgame(source.FullPath, destination, profileDataPath);
        var launchDestination = layerPlan.UsesFsgameLaunchArgument
            ? FileSystemSafety.ResolvePathInside(
                manifest.WriteOverlayRoot,
                layerPlan.FsgameLaunchRelativePath,
                "fsgame.ltx from -fsltx")
            : destination;
        if (!launchDestination.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            ProfileDataConfigurator.WriteProfileFsgame(source.FullPath, launchDestination, profileDataPath);
        }
        Directory.CreateDirectory(layerPlan.GameDataRoot);
        if (layerPlan.UsesSharedGameData)
        {
            progress?.Report($"USVFS: общие данные базовой игры: {layerPlan.GameDataRoot}. Существующие файлы сохранены.");
            return launchDestination;
        }
        ProfileDataConfigurator.EnsureProfileUserLtx(
            layerPlan,
            profileDataPath,
            progress);
        ProfileShaderCacheSeeder.Seed(layerPlan, profileDataPath, progress, cancellationToken);
        progress?.Report($"USVFS: профильный fsgame.ltx подготовлен из слоя «{source.SourceName}»{(layerPlan.ManualFsgameSource is null ? string.Empty : " (ручной выбор)")}.");
        return launchDestination;
    }
}
