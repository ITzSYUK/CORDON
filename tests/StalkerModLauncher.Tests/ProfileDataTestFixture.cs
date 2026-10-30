using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Tests;

internal static class ProfileDataTestFixture
{
    internal static Task<PreparedProfileData> PrepareAsync(WorkspaceBuilder builder, string game, ModProfile profile)
    {
        profile.WorkspacePath = builder.EnsureProfileWorkspace(profile, game);
        var layers = FileLayerPlan.CreateForProfile(game, profile, profile.WorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(layers, manifest, profile.WorkspacePath);
        var runtimeRoot = manifest.WriteOverlayRoot;
        if (layers.UsesAnomalyAppDataAlias)
        {
            runtimeRoot = UsvfsBootstrapPathResolver.Resolve(profile.WorkspacePath);
            UsvfsExecutableBootstrapper.Clear(profile.WorkspacePath, runtimeRoot);
            UsvfsExecutableBootstrapper.PrepareAnomalyLauncher(layers,
                new UsvfsLaunchTarget(Path.Combine(game, profile.ExecutableRelativePath),
                    profile.ExecutableRelativePath, string.Empty, game, "fixture", false), runtimeRoot);
        }
        return Task.FromResult(new PreparedProfileData(runtimeRoot, profile.WorkspacePath, string.Empty));
    }
}

internal sealed record PreparedProfileData(string WorkspaceRoot, string ProfileWorkspacePath, string WorkingDirectoryRelative);
