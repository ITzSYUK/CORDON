using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherProfileSettingsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavePreservesUsvfsBackendAndAnomalyRendererOverride()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "AnomalyLauncher.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "fsgame.ltx"), "$app_data_root$ = true | false | _appdata_\\");
        var profile = new ModProfile
        {
            Name = "Anomaly",
            GameInstallPath = _root,
            ExecutableRelativePath = "AnomalyLauncher.exe"
        };
        var saved = false;
        var viewModel = new ProfileSettingsViewModel(
            profile,
            new DialogService(),
            () =>
            {
                saved = true;
                return Task.CompletedTask;
            },
            _ => null,
            () => null,
            _ => false,
            usvfsAvailable: true);

        viewModel.UseVirtualFileSystem = true;
        viewModel.UseAnomalyDx9 = true;
        viewModel.AnomalyUseAvx = true;

        var result = await viewModel.TrySaveAsync();

        Assert.True(result);
        Assert.True(saved);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, profile.LaunchBackendKind);
        Assert.Equal(@"bin\AnomalyDX9AVX.exe", profile.UsvfsExecutableOverrideRelativePath);
        Assert.Empty(profile.ExecutableSourcePath);
    }

    [Fact]
    public async Task SaveStandaloneProfileAlwaysUsesLinkedWorkspace()
    {
        var profile = new ModProfile
        {
            Name = "Standalone",
            ExecutableRelativePath = "game.exe",
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };
        var viewModel = new ProfileSettingsViewModel(
            profile,
            new DialogService(),
            () => Task.CompletedTask,
            _ => null,
            () => null,
            _ => false,
            usvfsAvailable: true);

        viewModel.IsStandalone = true;
        var result = await viewModel.TrySaveAsync();

        Assert.True(result);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, profile.LaunchBackendKind);
    }

    [Fact]
    public async Task SaveSharedDataUsesManuallySelectedNamedFsgame()
    {
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        var fsgame = Path.Combine(mod, "fsgame_coc.ltx");
        File.WriteAllText(fsgame, "$app_data_root$ = true | false | _appdata_\\");
        var profile = new ModProfile
        {
            Name = "Call Of Chernobyl",
            GameInstallPath = game,
            ExecutableRelativePath = "xrEngine.exe",
            FsgameSourcePath = fsgame
        };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true });
        var saved = false;
        var viewModel = new ProfileSettingsViewModel(
            profile,
            new DialogService(),
            () =>
            {
                saved = true;
                return Task.CompletedTask;
            },
            _ => null,
            () => null,
            _ => false);

        viewModel.UseBaseGameData = true;
        var result = await viewModel.TrySaveAsync();

        Assert.True(result);
        Assert.True(saved);
        Assert.True(profile.UseBaseGameData);
        Assert.Equal(fsgame, profile.FsgameSourcePath);
    }

    [Fact]
    public async Task SaveWithoutFsgameShowsErrorAndDoesNotPersist()
    {
        var game = Path.Combine(_root, "missing-fsgame");
        Directory.CreateDirectory(game);
        var profile = new ModProfile
        {
            Name = "Missing fsgame",
            GameInstallPath = game,
            ExecutableRelativePath = "xrEngine.exe"
        };
        var saved = false;
        var dialogs = new RecordingDialogService();
        var viewModel = new ProfileSettingsViewModel(
            profile,
            dialogs,
            () =>
            {
                saved = true;
                return Task.CompletedTask;
            },
            _ => null,
            () => null,
            _ => false);

        var result = await viewModel.TrySaveAsync();

        Assert.False(result);
        Assert.False(saved);
        Assert.Equal("Не удалось сохранить настройки профиля", dialogs.ErrorTitle);
        Assert.Equal(ProfileDataConfigurator.MissingFsgameMessage, dialogs.ErrorMessage);
    }

    [Fact]
    public void SharedDataDescriptionUsesNamedFsgameFromFsltx()
    {
        var game = Path.Combine(_root, "fsltx-game");
        Directory.CreateDirectory(game);
        File.WriteAllText(
            Path.Combine(game, "fsgame_coc.ltx"),
            "$app_data_root$ = true | false | $fs_root$ | coc-data");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            LaunchArguments = "-fsltx fsgame_coc.ltx",
            UseBaseGameData = true
        };
        var viewModel = new ProfileSettingsViewModel(
            profile,
            new DialogService(),
            () => Task.CompletedTask,
            _ => null,
            () => null,
            _ => false);

        Assert.Contains(Path.Combine(game, "coc-data"), viewModel.GameDataDescription);
    }

    [Fact]
    public async Task SaveWhenPersistenceFailsKeepsWindowOpenAndRestoresProfile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "fsgame.ltx"), "$app_data_root$ = true | false | _appdata_\\");
        var profile = new ModProfile
        {
            Name = "Original",
            Description = "Saved description",
            GameInstallPath = _root,
            ExecutableRelativePath = @"bin\game.exe",
            LaunchArguments = "-saved",
            LaunchBackendKind = LaunchBackendKind.LinkedWorkspace
        };
        var dialogs = new RecordingDialogService();
        var viewModel = new ProfileSettingsViewModel(
            profile,
            dialogs,
            () => throw new SettingsPersistenceException("settings.json is locked"),
            _ => null,
            () => null,
            _ => false,
            usvfsAvailable: true)
        {
            ProfileName = "Changed",
            ProfileDescription = "Unsaved description",
            LaunchArguments = "-unsaved",
            UseVirtualFileSystem = true
        };

        var result = await viewModel.TrySaveAsync();

        Assert.False(result);
        Assert.Equal("Original", profile.Name);
        Assert.Equal("Saved description", profile.Description);
        Assert.Equal("-saved", profile.LaunchArguments);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, profile.LaunchBackendKind);
        Assert.Equal("Не удалось сохранить настройки профиля", dialogs.ErrorTitle);
        Assert.Contains("settings.json is locked", dialogs.ErrorMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingDialogService : DialogService
    {
        public string? ErrorTitle { get; private set; }
        public string? ErrorMessage { get; private set; }

        public override void ShowError(string title, string message)
        {
            ErrorTitle = title;
            ErrorMessage = message;
        }
    }
}
