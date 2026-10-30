using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class AnomalyAppDataAliasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmlAlias", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UsvfsUsesOneFileAndStorageSwitchDoesNotCopySettings()
    {
        var (builder, profile) = CreateProfile();
        var mod = Path.Combine(_root, "mod");
        Write(Path.Combine(mod, "appdata", "user.ltx"), "mod settings");
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true, Order = 1 });
        var first = await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var data = Path.Combine(profile.WorkspacePath, "userdata");
        var user = Path.Combine(data, "user.ltx");
        var aliasUser = Path.Combine(first.WorkspaceRoot, "appdata", "user.ltx");
        var sourceUser = Path.Combine(profile.GameInstallPath, "appdata", "user.ltx");
        Assert.Equal("mod settings", File.ReadAllText(user));
        Write(aliasUser, "workspace settings");
        Assert.Equal("workspace settings", File.ReadAllText(user));
        Assert.Equal("source settings", File.ReadAllText(sourceUser));

        var layers = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, profile.WorkspacePath);
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
        await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        Assert.Equal("usvfs settings", File.ReadAllText(aliasUser));

        profile.UseBaseGameData = true;
        await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        Assert.Equal("source settings", File.ReadAllText(aliasUser));
        Write(aliasUser, "shared settings");
        Assert.Equal("shared settings", File.ReadAllText(sourceUser));
        Assert.Equal("mod settings", File.ReadAllText(Path.Combine(mod, "appdata", "user.ltx")));
        Assert.Equal("usvfs settings", File.ReadAllText(user));
        profile.UseBaseGameData = false;
        await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        Assert.Equal("usvfs settings", File.ReadAllText(aliasUser));

        Write(Path.Combine(data, "savedgames", "keep.sav"), "save");
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
        var first = await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var layers = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, profile.WorkspacePath);
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
        var first = await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
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
    public async Task LegacyMigrationRefusesDirectoryLinksToSourceData()
    {
        var (builder, profile) = CreateProfile();
        var first = await ProfileDataTestFixture.PrepareAsync(builder, profile.GameInstallPath, profile);
        profile.UseBaseGameData = true;
        var layers = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, first.ProfileWorkspacePath);
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

        Assert.Throws<InvalidOperationException>(() => FileLayerPlan.CreateForProfile(
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
