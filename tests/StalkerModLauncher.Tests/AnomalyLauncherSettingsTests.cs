using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class AnomalyLauncherSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmlConfig", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceBuilder _builder;
    private readonly ModProfile _profile;
    private readonly string _game;

    public AnomalyLauncherSettingsTests()
    {
        _game = Path.Combine(_root, "game");
        Write(Path.Combine(_game, "AnomalyLauncher.exe"), "fixture launcher");
        Write(Path.Combine(_game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
        foreach (var name in new[] { "AnomalyLauncher.cfg", "commandline.txt", "launcherconfig.toml" })
            Write(Path.Combine(_game, name), "source");
        _profile = new ModProfile { GameInstallPath = _game, ExecutableRelativePath = "AnomalyLauncher.exe" };
        _builder = new WorkspaceBuilder(new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false));
    }

    [Theory]
    [InlineData("AnomalyLauncher.cfg", false)]
    [InlineData("AnomalyLauncher.cfg", true)]
    [InlineData("commandline.txt", false)]
    [InlineData("commandline.txt", true)]
    [InlineData("launcherconfig.toml", false)]
    [InlineData("launcherconfig.toml", true)]
    public async Task BootstrapReplacementSurvivesPreparationEvenWithOriginalTimestamp(string name, bool shared)
    {
        _profile.UseBaseGameData = shared;
        var first = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        var file = Path.Combine(first.WorkspaceRoot, name);
        var timestamp = File.GetLastWriteTimeUtc(file);
        var temporary = file + ".tmp";
        Write(temporary, "runtime settings");
        File.SetLastWriteTimeUtc(temporary, timestamp);
        File.Move(temporary, file, true);

        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);

        Assert.Equal("runtime settings", File.ReadAllText(file));
        Assert.Equal("runtime settings", File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, name)));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_game, name)));
    }

    [Fact]
    public async Task UnchangedBootstrapDoesNotOverwriteSettingsWrittenThroughUsvfs()
    {
        var first = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        var stored = ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, "AnomalyLauncher.cfg");
        Write(stored, "virtual write");
        File.SetLastWriteTimeUtc(stored, DateTime.UtcNow.AddYears(-1));

        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);

        Assert.Equal("virtual write", File.ReadAllText(Path.Combine(first.WorkspaceRoot, "AnomalyLauncher.cfg")));
    }

    [Fact]
    public async Task IndependentEditsKeepBothVersions()
    {
        var first = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        Write(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, "AnomalyLauncher.cfg"), "stored edit");
        Write(Path.Combine(first.WorkspaceRoot, "AnomalyLauncher.cfg"), "physical edit");

        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);

        Assert.Equal("physical edit", File.ReadAllText(Path.Combine(first.WorkspaceRoot, "AnomalyLauncher.cfg")));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_profile.WorkspacePath, "diagnostics"), "*.conflict-*.bak"));
        Assert.Equal("stored edit", File.ReadAllText(backup));
    }

    [Fact]
    public async Task MoveCapturesPhysicalBootstrapBeforeRemovingOldDirectory()
    {
        var first = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        Write(Path.Combine(first.WorkspaceRoot, "launcherconfig.toml"), "latest");
        var old = _profile.WorkspacePath;

        var result = await new WorkspaceManagementService(_builder).MoveAsync(_profile, Path.Combine(_root, "moved"), new Progress<string>());

        Assert.Null(result.CleanupFailure);
        Assert.False(Directory.Exists(old));
        Assert.Equal("latest", File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, "launcherconfig.toml")));
    }

    [Fact]
    public async Task DoesNotImportLegacyCurrent()
    {
        _profile.WorkspacePath = _builder.EnsureProfileWorkspace(_profile, _game);
        var legacy = Path.Combine(_profile.WorkspacePath, "current", "AnomalyLauncher.cfg");
        Write(legacy, "legacy workspace");

        var prepared = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);

        Assert.Equal("source", File.ReadAllText(Path.Combine(prepared.WorkspaceRoot, "AnomalyLauncher.cfg")));
        Assert.Equal("legacy workspace", File.ReadAllText(legacy));
    }

    [Fact]
    public async Task ReadOnlyUserSettingsAreSeededAsWritableIndependentFile()
    {
        var source = Path.Combine(_game, "appdata", "user.ltx");
        Write(source, "source user");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
            var user = Path.Combine(_profile.WorkspacePath, "userdata", "user.ltx");
            Assert.False(File.GetAttributes(user).HasFlag(FileAttributes.ReadOnly));
            File.WriteAllText(user, "changed");
            Assert.Equal("source user", File.ReadAllText(source));
        }
        finally { File.SetAttributes(source, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RecompiledShaderCacheSurvivesSubsequentPreparation()
    {
        Write(Path.Combine(_game, "appdata", "shaders_cache", "r4", "test.bin"), "shipped cache");
        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        var cache = Path.Combine(_profile.WorkspacePath, "userdata", "shaders_cache", "r4", "test.bin");
        Write(cache, "engine cache");
        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        Assert.Equal("engine cache", File.ReadAllText(cache));
        File.Delete(cache);
        await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        Assert.Equal("shipped cache", File.ReadAllText(cache));
    }

    [Fact]
    public async Task MovePreservesConflictBackupAlongsideSelectedSettings()
    {
        var first = await ProfileDataTestFixture.PrepareAsync(_builder, _game, _profile);
        Write(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, "AnomalyLauncher.cfg"), "stored edit");
        Write(Path.Combine(first.WorkspaceRoot, "AnomalyLauncher.cfg"), "physical edit");
        var result = await new WorkspaceManagementService(_builder).MoveAsync(_profile, Path.Combine(_root, "move-conflict"), new Progress<string>());
        Assert.Null(result.CleanupFailure);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_profile.WorkspacePath, "diagnostics"), "*.conflict-*.bak"));
        Assert.Equal("stored edit", File.ReadAllText(backup));
        Assert.Equal("physical edit", File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(_profile.WorkspacePath, "AnomalyLauncher.cfg")));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profile.WorkspacePath))
            AnomalyAppDataAlias.Detach(Path.Combine(_profile.WorkspacePath, ".usvfs-bootstrap"));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
