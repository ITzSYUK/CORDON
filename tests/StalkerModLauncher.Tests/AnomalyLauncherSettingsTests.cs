using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class AnomalyLauncherSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmlConfig", Guid.NewGuid().ToString("N"));
    private readonly DateTime _sourceTime = DateTime.UtcNow.AddMinutes(-10);
    private readonly WorkspaceBuilder _builder;
    private readonly ModProfile _profile;
    private readonly string _game;
    private readonly string _mod;

    public AnomalyLauncherSettingsTests()
    {
        _game = Path.Combine(_root, "game");
        _mod = Path.Combine(_root, "mod");
        Write(Path.Combine(_game, "AnomalyLauncher.exe"), "fixture launcher", _sourceTime);
        Write(Path.Combine(_game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata", _sourceTime);
        Write(Path.Combine(_game, "AnomalyLauncher.cfg"), "base launcher", _sourceTime);
        Write(Path.Combine(_game, "commandline.txt"), "-smap1024", _sourceTime);
        Write(Path.Combine(_mod, "AnomalyLauncher.cfg"), "mod launcher", _sourceTime);
        Write(Path.Combine(_mod, "commandline.txt"), "-smap2048", _sourceTime);
        Write(Path.Combine(_game, "launcherconfig.toml"), "base launcher", _sourceTime);
        Write(Path.Combine(_mod, "launcherconfig.toml"), "mod launcher", _sourceTime);
        _profile = new ModProfile { GameInstallPath = _game, ExecutableRelativePath = "AnomalyLauncher.exe" };
        _profile.Mods.Add(new ModEntry { SourcePath = _mod, IsEnabled = true, Order = 1 });
        _builder = new WorkspaceBuilder(new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), preferGameDriveWorkspace: false));
        _profile.WorkspacePath = _builder.EnsureProfileWorkspace(_profile, _game);
    }

    [Theory]
    [InlineData("AnomalyLauncher.cfg", false)]
    [InlineData("AnomalyLauncher.cfg", true)]
    [InlineData("launcherconfig.toml", false)]
    [InlineData("launcherconfig.toml", true)]
    public async Task SettingsSurviveWorkspaceUsvfsWorkspaceAndReturnToUsvfs(string settingsFile, bool sharedData)
    {
        _profile.UseBaseGameData = sharedData;
        var first = await _builder.BuildAsync(_game, _profile, new ProgressLog());
        var workspace = first.WorkspaceRoot;
        Assert.Equal("mod launcher", File.ReadAllText(Path.Combine(workspace, settingsFile)));
        Assert.Equal("-smap2048", File.ReadAllText(Path.Combine(workspace, "commandline.txt")));
        var manifestPath = Path.Combine(_profile.WorkspacePath, "build-manifest.json");
        var originalManifest = File.ReadAllText(manifestPath);
        Write(Path.Combine(workspace, settingsFile), "workspace launcher", _sourceTime.AddMinutes(1));
        Write(Path.Combine(workspace, "commandline.txt"), "-smap3072", _sourceTime.AddMinutes(1));
        var layers = FileLayerPlan.CreateLinkedWorkspace(_game, _profile, _profile.WorkspacePath);
        var bootstrap = PrepareUsvfs(layers);
        Assert.Equal("workspace launcher", File.ReadAllText(Path.Combine(bootstrap, settingsFile)));
        Assert.Equal("-smap3072", File.ReadAllText(Path.Combine(bootstrap, "commandline.txt")));

        var temporary = Path.Combine(bootstrap, "launcher.tmp");
        Write(temporary, "usvfs launcher", _sourceTime.AddMinutes(2));
        File.Move(temporary, Path.Combine(bootstrap, settingsFile), overwrite: true);
        Write(Path.Combine(bootstrap, "commandline.txt"), "-smap4096", _sourceTime.AddMinutes(2));
        var progress = new ProgressLog();
        await _builder.BuildAsync(_game, _profile, progress);

        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal(originalManifest, File.ReadAllText(manifestPath));
        Assert.Equal("usvfs launcher", File.ReadAllText(Path.Combine(workspace, settingsFile)));
        Assert.Equal("-smap4096", File.ReadAllText(Path.Combine(workspace, "commandline.txt")));
        Write(Path.Combine(workspace, "commandline.txt"), "-smap1536", _sourceTime.AddMinutes(3));
        bootstrap = PrepareUsvfs(layers);
        Assert.Equal("-smap1536", File.ReadAllText(Path.Combine(bootstrap, "commandline.txt")));
        Assert.Equal("base launcher", File.ReadAllText(Path.Combine(_game, settingsFile)));
        Assert.Equal("-smap1024", File.ReadAllText(Path.Combine(_game, "commandline.txt")));
        Assert.Equal("mod launcher", File.ReadAllText(Path.Combine(_mod, settingsFile)));
        Assert.Equal("-smap2048", File.ReadAllText(Path.Combine(_mod, "commandline.txt")));
    }

    [Theory]
    [InlineData("AnomalyLauncher.cfg", false)]
    [InlineData("AnomalyLauncher.cfg", true)]
    [InlineData("commandline.txt", false)]
    [InlineData("commandline.txt", true)]
    [InlineData("launcherconfig.toml", false)]
    [InlineData("launcherconfig.toml", true)]
    public async Task MigratesLegacyOverwriteAndKeepsTheNewerRuntimeVersion(string fileName, bool newerWorkspace)
    {
        var first = await _builder.BuildAsync(_game, _profile, new ProgressLog());
        var overwrite = Path.Combine(_profile.WorkspacePath, "userdata", "overwrite", fileName);
        var current = Path.Combine(first.WorkspaceRoot, fileName);
        Write(overwrite, "legacy usvfs", _sourceTime.AddMinutes(2));
        Write(current, "workspace", _sourceTime.AddMinutes(newerWorkspace ? 3 : 1));
        var progress = new ProgressLog();

        await _builder.BuildAsync(_game, _profile, progress);

        var expected = newerWorkspace ? "workspace" : "legacy usvfs";
        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal(expected, File.ReadAllText(current));
        Assert.Equal(expected, File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, fileName)));
        Assert.False(File.Exists(overwrite));
        Write(overwrite, "stale overwrite", _sourceTime);
        await _builder.BuildAsync(_game, _profile, new ProgressLog());
        Assert.Equal(expected, File.ReadAllText(current));
        Assert.False(File.Exists(overwrite));
    }

    [Theory]
    [InlineData("AnomalyLauncher.cfg")]
    [InlineData("commandline.txt")]
    [InlineData("launcherconfig.toml")]
    public async Task DetachesLegacySourceLinkWithoutRebuilding(string fileName)
    {
        var first = await _builder.BuildAsync(_game, _profile, new ProgressLog());
        var source = Path.Combine(_mod, fileName);
        var sourceContents = File.ReadAllText(source);
        var current = Path.Combine(first.WorkspaceRoot, fileName);
        WorkspaceMaterializer.ReplaceFile(source, first.WorkspaceRoot, "legacy-link.txt", new WorkspaceBuildStats());
        File.Delete(current);
        File.Move(Path.Combine(first.WorkspaceRoot, "legacy-link.txt"), current);
        var progress = new ProgressLog();

        await _builder.BuildAsync(_game, _profile, progress);
        File.WriteAllText(current, "changed settings");

        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.Equal(sourceContents, File.ReadAllText(source));
    }

    [Fact]
    public async Task TomlCreatedOnlyInOverwriteIsMigratedAndAvailableInBothBackends()
    {
        const string fileName = "launcherconfig.toml";
        File.Delete(Path.Combine(_game, fileName));
        File.Delete(Path.Combine(_mod, fileName));
        var overwrite = Path.Combine(_profile.WorkspacePath, "userdata", "overwrite", fileName);
        const string contents = "shadow_map = \"Size1536\"\nwindow_mode = \"Fullscreen\"";
        Write(overwrite, contents, _sourceTime.AddMinutes(1));

        var built = await _builder.BuildAsync(_game, _profile, new ProgressLog());
        var layers = FileLayerPlan.CreateLinkedWorkspace(_game, _profile, _profile.WorkspacePath);
        var bootstrap = PrepareUsvfs(layers);

        Assert.Equal(contents, File.ReadAllText(Path.Combine(built.WorkspaceRoot, fileName)));
        Assert.Equal(contents, File.ReadAllText(Path.Combine(bootstrap, fileName)));
        Assert.Equal(contents, File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, fileName)));
        Assert.False(File.Exists(overwrite));
        Assert.False(File.Exists(Path.Combine(_game, fileName)));
        Assert.False(File.Exists(Path.Combine(_mod, fileName)));
    }

    private string PrepareUsvfs(FileLayerPlan layers)
    {
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(_profile, layers, _profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(layers, manifest, _profile.WorkspacePath);
        var resolution = ProfileLaunchPlanResolver.PreviewVirtualFileSystem(_profile, layers);
        var target = AnomalyUsvfsLaunchTargetResolver.Resolve(_profile, layers, resolution);
        var bootstrap = Path.Combine(_profile.WorkspacePath, ".usvfs-bootstrap");
        UsvfsExecutableBootstrapper.Clear(_profile.WorkspacePath, bootstrap);
        UsvfsExecutableBootstrapper.PrepareAnomalyLauncher(layers, target, bootstrap);
        return bootstrap;
    }

    private static void Write(string path, string contents, DateTime timestamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    public void Dispose()
    {
        FileSystemSafety.DeleteDirectoryContents(_profile.WorkspacePath, Path.Combine(_root, "workspaces"));
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ProgressLog : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
}
