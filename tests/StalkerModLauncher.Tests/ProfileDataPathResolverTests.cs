using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileDataPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetLogDirectoriesUsesWorkspaceForOverlayProfile()
    {
        var profile = new ModProfile { WorkspacePath = _root };

        var result = ProfileDataPathResolver.GetLogDirectories(profile);

        Assert.Equal([Path.Combine(_root, "userdata", "logs")], result);
    }

    [Fact]
    public void GetLogDirectoriesIncludesStandardStandaloneLocations()
    {
        Directory.CreateDirectory(_root);
        var profile = CreateStandaloneProfile();

        var result = ProfileDataPathResolver.GetLogDirectories(profile);

        Assert.Contains(Path.Combine(_root, "appdata", "logs"), result);
        Assert.Contains(Path.Combine(_root, "bin_x64", "_appdata_", "logs"), result);
    }

    [Fact]
    public void GetLogDirectoriesResolvesFourPartFsgameAppDataRoot()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "fsgame.ltx"),
            "$app_data_root$ = true | false | $fs_root$ | custom_data");
        var profile = CreateStandaloneProfile();

        var result = ProfileDataPathResolver.GetLogDirectories(profile);

        Assert.Contains(Path.Combine(_root, "custom_data", "logs"), result);
    }

    [Fact]
    public void GetGameDataRootUsesManuallySelectedNamedFsgame()
    {
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        var fsgame = Path.Combine(mod, "bin", "fsgame_coc.ltx");
        Directory.CreateDirectory(Path.GetDirectoryName(fsgame)!);
        File.WriteAllText(fsgame, "$app_data_root$ = true | false | $fs_root$ | _appdata_\\");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            UseBaseGameData = true,
            FsgameSourcePath = fsgame
        };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true });

        var result = ProfileDataPathResolver.GetGameDataRoot(profile);

        Assert.Equal(Path.Combine(game, "_appdata_") + Path.DirectorySeparatorChar, result);
    }

    [Fact]
    public void SharedDataDiscoveryUsesNamedFsgameFromFsltx()
    {
        var game = Path.Combine(_root, "fsltx-game");
        var mod = Path.Combine(_root, "fsltx-mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        File.WriteAllText(
            Path.Combine(game, "fsgame.ltx"),
            "$app_data_root$ = true | false | $fs_root$ | wrong-data");
        File.WriteAllText(
            Path.Combine(mod, "fsgame_coc.ltx"),
            "$app_data_root$ = true | false | $fs_root$ | coc-data");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            UseBaseGameData = true,
            LaunchArguments = "-fsltx fsgame_coc.ltx"
        };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = true, Order = 1 });

        var dataRoot = ProfileDataPathResolver.GetGameDataRoot(profile);
        var screenshotRoot = Assert.Single(ProfileDataPathResolver.GetScreenshotDirectories(profile));

        Assert.Equal(Path.Combine(game, "coc-data"), dataRoot);
        Assert.Equal(Path.Combine(game, "coc-data", "screenshots"), screenshotRoot);
    }

    [Fact]
    public void DisabledManualFsgameSourceIsHandledAsInvalidConfiguration()
    {
        var game = Path.Combine(_root, "disabled-game");
        var mod = Path.Combine(_root, "disabled-mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        var fsgame = Path.Combine(mod, "fsgame_coc.ltx");
        File.WriteAllText(fsgame, "$app_data_root$ = true | false | $fs_root$ | _appdata_\\");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            UseBaseGameData = true,
            FsgameSourcePath = fsgame
        };
        profile.Mods.Add(new ModEntry { SourcePath = mod, IsEnabled = false });

        Assert.Throws<InvalidDataException>(() => ProfileDataPathResolver.GetGameDataRoot(profile));
        Assert.Empty(ProfileDataPathResolver.GetScreenshotDirectories(profile));
    }

    [Fact]
    public void GetScreenshotDirectoriesUsesResolvedDataRoots()
    {
        Directory.CreateDirectory(_root);
        var profile = CreateStandaloneProfile();

        var result = ProfileDataPathResolver.GetScreenshotDirectories(profile);

        Assert.Contains(Path.Combine(_root, "appdata", "screenshots"), result);
        Assert.Contains(Path.Combine(_root, "bin_x64", "_appdata_", "screenshots"), result);
    }

    private ModProfile CreateStandaloneProfile()
    {
        var profile = new ModProfile { IsStandalone = true };
        profile.Mods.Add(new ModEntry { SourcePath = _root, IsEnabled = true });
        return profile;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
