using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ScreenshotScannerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanAsyncUsesOnlyTheSelectedGameDataDirectory()
    {
        var workspace = Path.Combine(_root, "workspace");
        var game = Path.Combine(_root, "game");
        var profileScreenshot = CreateFile(workspace, "userdata", "screenshots", "profile.png");
        var gameScreenshot = CreateFile(game, "appdata", "screenshots", "game.jpg");
        CreateFile(game, "appdata", "screenshots", "ignored.dds");
        var profile = new ModProfile { WorkspacePath = workspace, GameInstallPath = game };
        File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
        var result = await ScreenshotScannerService.ScanAsync(profile);

        Assert.Equal(profileScreenshot, Assert.Single(result));
        profile.UseBaseGameData = true;
        result = await ScreenshotScannerService.ScanAsync(profile);
        Assert.Equal(gameScreenshot, Assert.Single(result));
    }

    [Fact]
    public async Task ScanAsyncUsesStandaloneDataLocations()
    {
        var screenshot = CreateFile(_root, "bin_x64", "_appdata_", "screenshots", "standalone.bmp");
        var profile = new ModProfile { IsStandalone = true };
        profile.Mods.Add(new ModEntry { SourcePath = _root, IsEnabled = true });
        var result = await ScreenshotScannerService.ScanAsync(profile);

        Assert.Contains(screenshot, result);
    }

    [Fact]
    public async Task ScanAsyncHonorsCancellation()
    {
        var profile = new ModProfile { WorkspacePath = _root };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScreenshotScannerService.ScanAsync(profile, cancellation.Token));
    }

    private static string CreateFile(string root, params string[] parts)
    {
        var path = parts.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
