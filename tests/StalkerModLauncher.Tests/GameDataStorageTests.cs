using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class GameDataStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonDataTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkspaceSwitchesDataWithoutOverwritingEitherDirectoryAndDeletionKeepsSharedSaves()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "bin/xr_3da.exe", "exe");
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | $fs_root$ | appdata");
        Write(game, "appdata/user.ltx", "shared settings");
        Write(game, "appdata/savedgames/shared.sav", "shared save");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = new ModProfile { GameInstallPath = game };
        var progress = new Progress<string>();
        var first = await builder.BuildAsync(game, profile, progress);
        Write(profile.WorkspacePath, "userdata/user.ltx", "private settings");
        Write(profile.WorkspacePath, "userdata/savedgames/private.sav", "private save");
        profile.UseBaseGameData = true;
        var second = await builder.BuildAsync(game, profile, progress);
        Assert.Contains(Path.Combine(game, "appdata"), File.ReadAllText(Path.Combine(second.WorkspaceRoot, "fsgame.ltx")));
        Assert.Equal(Path.Combine(game, "appdata", "savedgames"), WorkspaceBuilder.GetSavedGamesPath(profile));
        Assert.Equal("shared settings", File.ReadAllText(Path.Combine(game, "appdata", "user.ltx")));
        Assert.False(File.Exists(Path.Combine(game, "appdata", "savedgames", "private.sav")));
        profile.UseBaseGameData = false;
        await builder.BuildAsync(game, profile, progress);
        Assert.Contains(Path.Combine(profile.WorkspacePath, "userdata"), File.ReadAllText(Path.Combine(first.WorkspaceRoot, "fsgame.ltx")));
        Assert.Equal("private settings", File.ReadAllText(Path.Combine(profile.WorkspacePath, "userdata", "user.ltx")));
        profile.UseBaseGameData = true;
        builder.DeleteProfileWorkspace(profile, game);
        Assert.Equal("shared save", File.ReadAllText(Path.Combine(game, "appdata", "savedgames", "shared.sav")));
        Assert.Equal("$app_data_root$ = true | false | $fs_root$ | appdata", File.ReadAllText(Path.Combine(game, "fsgame.ltx")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsvfsOverridesRootOverwriteForSharedData(bool anomalyBootstrap)
    {
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mod");
        var workspace = Path.Combine(_root, "workspace");
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | _appdata_");
        Write(game, "_appdata_/user.ltx", "existing");
        Write(mod, "_appdata_/user.ltx", "must not replace existing");
        var profile = new ModProfile { GameInstallPath = game, WorkspacePath = workspace, UseBaseGameData = true };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true, Order = 1 });
        var layers = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, workspace);
        var config = UsvfsProfileDataPreparer.Prepare(layers, manifest, workspace)!;
        var mapping = anomalyBootstrap
            ? UsvfsMappingPlanBuilder.BuildAnomalyLauncherBootstrap(layers, manifest, Path.Combine(workspace, "bootstrap"))
            : UsvfsMappingPlanBuilder.Build(layers, manifest);
        Assert.DoesNotContain(mapping.Operations, operation => operation.SourceName == "shared game data");
        Assert.Contains(Path.Combine(game, "_appdata_"), File.ReadAllText(config));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(game, "_appdata_", "user.ltx")));
        Assert.Equal(Path.Combine(workspace, "userdata", "overwrite"), manifest.WriteOverlayRoot);
        Assert.Equal(Path.Combine(game, "_appdata_", "screenshots"), Assert.Single(ProfileDataPathResolver.GetScreenshotDirectories(profile)));
    }

    [Fact]
    public void ResolvesAliasesAndRejectsUnknownOrCyclicPaths()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "fsgame.ltx", "$data$ = true | false | $fs_root$ | common\n$app_data_root$ = true | false | $data$ | saves ; comment");
        Assert.Equal(Path.Combine(game, "common", "saves"), ProfileAppDataSourceLocator.ResolveConfiguredRoot(game));
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | $unknown$ | saves");
        Assert.Throws<InvalidDataException>(() => ProfileAppDataSourceLocator.ResolveConfiguredRoot(game));
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | $app_data_root$ | saves");
        Assert.Throws<InvalidDataException>(() => ProfileAppDataSourceLocator.ResolveConfiguredRoot(game));
    }

    [Fact]
    public void StandaloneDataDiscoveryResolvesConfiguredAliases()
    {
        var build = Path.Combine(_root, "standalone");
        Write(build, "fsgame.ltx", "$data$ = true | false | $fs_root$ | common\n$app_data_root$ = true | false | $data$ | saves");
        var profile = new ModProfile { IsStandalone = true };
        profile.Mods.Add(new ModEntry { SourcePath = build, IsEnabled = true });

        Assert.Contains(
            Path.Combine(build, "common", "saves", "screenshots"),
            ProfileDataPathResolver.GetScreenshotDirectories(profile));
    }

    [Fact]
    public void SharedDataRejectsOverlapWithModSource()
    {
        var game = Path.Combine(_root, "game-with-external-data");
        var mod = Path.Combine(_root, "mod-with-appdata");
        var sharedData = Path.Combine(mod, "appdata");
        Write(game, "fsgame.ltx", $"$app_data_root$ = true | false | {sharedData}");
        Directory.CreateDirectory(mod);
        var profile = new ModProfile { GameInstallPath = game, UseBaseGameData = true };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true });

        Assert.Throws<InvalidDataException>(() => ProfileDataPathResolver.GetGameDataRoot(profile));
    }

    [Fact]
    public void RootEnumerationFallsBackWhenConfiguredAliasIsInvalid()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | $unknown$");
        Write(game, "appdata/user.ltx", "settings");

        Assert.Equal(Path.Combine(game, "appdata"), Assert.Single(ProfileAppDataSourceLocator.EnumerateRoots(game)));
    }

    [Fact]
    public void IdentifiesOnlyVolumeRootsAsFileSystemRoots()
    {
        Assert.True(FileSystemSafety.IsFileSystemRoot(Path.GetPathRoot(_root)!));
        Assert.False(FileSystemSafety.IsFileSystemRoot(_root));
    }

    [Fact]
    public void ExplicitCopySkipsConflictsAndServiceStores()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "destination");
        Write(source, "user.ltx", "source settings");
        Write(destination, "user.ltx", "keep settings");
        Write(source, "savedgames/new.sav", "new save");
        Write(source, "overwrite/fsgame.ltx", "service config");
        Write(source, "writable-game-files/options.ltx", "service settings");
        var result = GameDataCopyService.CopyMissing(source, destination);
        Assert.Equal(1, result.Copied);
        Assert.Equal(3, result.Skipped);
        Assert.Equal("keep settings", File.ReadAllText(Path.Combine(destination, "user.ltx")));
        Assert.Equal("new save", File.ReadAllText(Path.Combine(destination, "savedgames", "new.sav")));
        Assert.False(Directory.Exists(Path.Combine(destination, "overwrite")));
        Assert.False(Directory.Exists(Path.Combine(destination, "writable-game-files")));
    }

    [Fact]
    public async Task InvalidSharedConfigIsReportedInsteadOfThrowingFromPreflight()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "bin/xr_3da.exe", "exe");
        Write(game, "fsgame.ltx", "$app_data_root$ = true | false | $unknown$");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var manager = new ProfileManager(paths, new WorkspaceBuilder(paths));
        var profile = new ModProfile { GameInstallPath = game, UseBaseGameData = true };
        var report = await new LaunchPreflightService(manager).AnalyzeAsync(profile);
        Assert.False(report.CanLaunch);
        Assert.Contains(report.Checks, check => check.Title == "Данные игры" && check.Status == ProfileHealthStatus.Error);
        var health = await new ProfileHealthService(manager).AnalyzeAsync(profile);
        Assert.Contains(health.Checks, check => check.Title == "Данные игры" && check.Status == ProfileHealthStatus.Error);
    }

    private static void Write(string root, string relative, string contents)
    {
        var file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
