using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class AnomalyAppDataAliasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmlAlias", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkspaceAndUsvfsUseOneFileAndStorageSwitchDoesNotCopySettings()
    {
        var (builder, profile) = CreateProfile();
        var mod = Path.Combine(_root, "mod");
        Write(Path.Combine(mod, "appdata", "user.ltx"), "mod settings");
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true, Order = 1 });
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var data = Path.Combine(profile.WorkspacePath, "userdata");
        var user = Path.Combine(data, "user.ltx");
        var aliasUser = Path.Combine(first.WorkspaceRoot, "appdata", "user.ltx");
        var sourceUser = Path.Combine(profile.GameInstallPath, "appdata", "user.ltx");
        var manifestPath = Path.Combine(profile.WorkspacePath, "build-manifest.json");
        var manifestText = File.ReadAllText(manifestPath);
        Assert.Equal("mod settings", File.ReadAllText(user));
        Write(aliasUser, "workspace settings");
        Assert.Equal("workspace settings", File.ReadAllText(user));
        Assert.Equal("source settings", File.ReadAllText(sourceUser));

        var layers = FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, profile.WorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(layers, manifest, profile.WorkspacePath);
        var bootstrap = Path.Combine(profile.WorkspacePath, ".usvfs-bootstrap");
        Directory.CreateDirectory(bootstrap);
        AnomalyAppDataAlias.Prepare(bootstrap, layers);
        var temporary = Path.Combine(bootstrap, "appdata", "user.tmp");
        Write(temporary, "usvfs settings");
        File.Move(temporary, Path.Combine(bootstrap, "appdata", "user.ltx"), overwrite: true);
        Assert.Equal("usvfs settings", File.ReadAllText(user));
        var progress = new ProgressLog();
        await builder.BuildAsync(profile.GameInstallPath, profile, progress);
        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal(manifestText, File.ReadAllText(manifestPath));
        Assert.Equal("usvfs settings", File.ReadAllText(aliasUser));

        profile.UseBaseGameData = true;
        await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        Assert.Equal("source settings", File.ReadAllText(aliasUser));
        Write(aliasUser, "shared settings");
        Assert.Equal("shared settings", File.ReadAllText(sourceUser));
        Assert.Equal("mod settings", File.ReadAllText(Path.Combine(mod, "appdata", "user.ltx")));
        Assert.Equal("usvfs settings", File.ReadAllText(user));
        profile.UseBaseGameData = false;
        await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        Assert.Equal("usvfs settings", File.ReadAllText(aliasUser));
        Assert.Equal(manifestText, File.ReadAllText(manifestPath));

        Write(Path.Combine(data, "savedgames", "keep.sav"), "save");
        builder.ClearProfileWorkspaceCache(profile, profile.GameInstallPath);
        Assert.Equal("usvfs settings", File.ReadAllText(user));
        Assert.Equal("save", File.ReadAllText(Path.Combine(data, "savedgames", "keep.sav")));
        Assert.Equal("shared settings", File.ReadAllText(sourceUser));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapAliasSurvivesReplacementAndCleanupForBothStorageModes(bool shared)
    {
        var (builder, profile) = CreateProfile();
        profile.UseBaseGameData = shared;
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var layers = FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, profile.WorkspacePath);
        var bootstrap = Path.Combine(profile.WorkspacePath, ".usvfs-bootstrap");
        Directory.CreateDirectory(bootstrap);
        AnomalyAppDataAlias.Prepare(bootstrap, layers);
        var aliasUser = Path.Combine(bootstrap, "appdata", "user.ltx");
        File.Delete(aliasUser);
        Write(aliasUser, "reset settings");
        Assert.Equal("reset settings", File.ReadAllText(Path.Combine(layers.GameDataRoot, "user.ltx")));
        FileSystemSafety.DeleteDirectoryContents(bootstrap, profile.WorkspacePath);
        Assert.Equal("reset settings", File.ReadAllText(Path.Combine(layers.GameDataRoot, "user.ltx")));
        if (shared)
        {
            builder.DeleteProfileWorkspace(profile, profile.GameInstallPath);
            Assert.Equal("reset settings", File.ReadAllText(Path.Combine(layers.GameDataRoot, "user.ltx")));
        }
    }

    [Fact]
    public async Task LegacyCopiesAreArchivedWithoutReplacingCanonicalSettings()
    {
        var (builder, profile) = CreateProfile();
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        var data = Path.Combine(first.ProfileWorkspacePath, "userdata");
        Write(Path.Combine(data, "user.ltx"), "canonical settings");
        var legacy = Path.Combine(data, "writable-game-files", "appdata", "user.ltx");
        var overwrite = Path.Combine(data, "overwrite", "appdata", "user.ltx");
        Write(legacy, "old launcher settings");
        Write(overwrite, "canonical settings");
        AnomalyAppDataAlias.ArchiveLegacyUserFiles(first.ProfileWorkspacePath, data);
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(overwrite));
        Assert.Equal("canonical settings", File.ReadAllText(Path.Combine(data, "user.ltx")));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(first.ProfileWorkspacePath, "diagnostics"), "*.bak"));
        Assert.Equal("old launcher settings", File.ReadAllText(backup));
    }

    [Fact]
    public async Task ReplacesLegacyPhysicalAppDataOnceAndKeepsUserSettings()
    {
        var (builder, profile) = CreateProfile();
        profile.GameInstallPath += Path.DirectorySeparatorChar;
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var data = Path.Combine(profile.WorkspacePath, "userdata");
        Write(Path.Combine(data, "user.ltx"), "keep settings");
        AnomalyAppDataAlias.Detach(first.WorkspaceRoot);
        Write(Path.Combine(first.WorkspaceRoot, "appdata", "user.ltx"), "legacy settings");

        await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        var progress = new ProgressLog();
        await builder.BuildAsync(profile.GameInstallPath, profile, progress);

        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal("keep settings", File.ReadAllText(Path.Combine(first.WorkspaceRoot, "appdata", "user.ltx")));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(profile.WorkspacePath, "diagnostics"), "*.bak"));
        Assert.Equal("legacy settings", File.ReadAllText(backup));
        Assert.Equal("source settings", File.ReadAllText(Path.Combine(profile.GameInstallPath, "appdata", "user.ltx")));
    }

    [Fact]
    public async Task CacheStillDetectsMissingFileSharedBySeveralLayers()
    {
        var (builder, profile) = CreateProfile();
        for (var i = 0; i < 2; i++)
        {
            var mod = Path.Combine(_root, $"mod-{i}");
            Write(Path.Combine(mod, "gamedata", "configs", "shared.ltx"), $"mod {i}");
            profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true, Order = i });
        }
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        var sharedFile = Path.Combine(first.WorkspaceRoot, "gamedata", "configs", "shared.ltx");
        File.Delete(sharedFile);
        var progress = new ProgressLog();

        await builder.BuildAsync(profile.GameInstallPath, profile, progress);

        Assert.DoesNotContain(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal("mod 1", File.ReadAllText(sharedFile));
    }

    [Fact]
    public async Task LegacyMigrationRefusesDirectoryLinksToSourceData()
    {
        var (builder, profile) = CreateProfile();
        var first = await builder.BuildAsync(profile.GameInstallPath, profile, new ProgressLog());
        profile.UseBaseGameData = true;
        var layers = FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, first.ProfileWorkspacePath);
        var legacyStore = Path.Combine(first.ProfileWorkspacePath, "userdata", "writable-game-files");
        AnomalyAppDataAlias.Prepare(legacyStore, layers);

        Assert.Throws<IOException>(() => AnomalyAppDataAlias.ArchiveLegacyUserFiles(first.ProfileWorkspacePath, layers.GameDataRoot));
        Assert.Equal("source settings", File.ReadAllText(Path.Combine(layers.GameDataRoot, "user.ltx")));
        AnomalyAppDataAlias.Detach(legacyStore);
    }

    [Fact]
    public void RejectsFsgameInsideRedirectedDataBeforeModifyingSources()
    {
        var (_, profile) = CreateProfile();
        var config = Path.Combine(profile.GameInstallPath, "appdata", "custom.ltx");
        const string contents = "$app_data_root$ = true | false | appdata";
        Write(config, contents);
        profile.FsgameSourcePath = config;

        Assert.Throws<InvalidOperationException>(() => FileLayerPlan.CreateLinkedWorkspace(
            profile.GameInstallPath, profile, Path.Combine(_root, "profile")));

        Assert.Equal(contents, File.ReadAllText(config));
    }

    private (WorkspaceBuilder Builder, ModProfile Profile) CreateProfile()
    {
        var game = Path.Combine(_root, "game");
        Write(Path.Combine(game, "AnomalyLauncher.exe"), "fixture launcher");
        Write(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
        Write(Path.Combine(game, "appdata", "user.ltx"), "source settings");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), preferGameDriveWorkspace: false);
        return (new WorkspaceBuilder(paths), new ModProfile { GameInstallPath = game, ExecutableRelativePath = "AnomalyLauncher.exe" });
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        var workspaces = Path.Combine(_root, "workspaces");
        if (Directory.Exists(workspaces))
            foreach (var profile in Directory.EnumerateDirectories(workspaces))
            {
                AnomalyAppDataAlias.Detach(Path.Combine(profile, "current"));
                AnomalyAppDataAlias.Detach(Path.Combine(profile, ".usvfs-bootstrap"));
            }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ProgressLog : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
}
