using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class FileLayerExplorerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherExplorerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BuildFinalTreeAsyncReportsWinnerProvidersAndExclusions()
    {
        CreateFile("game", "shared.ltx");
        CreateFile("first", "shared.ltx");
        CreateFile("patch", "shared.ltx");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game") };
        profile.Mods.Add(new ModEntry { Id = "first", Name = "First", SourcePath = Path.Combine(_root, "first"), Order = 1 });
        profile.Mods.Add(new ModEntry
        {
            Id = "patch",
            Name = "Patch",
            SourcePath = Path.Combine(_root, "patch"),
            Order = 2,
            ExcludedFiles = ["shared.ltx"]
        });
        var plan = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, Path.Combine(_root, "workspace"));

        var files = await FileLayerExplorerService.BuildFinalTreeAsync(plan);

        var shared = Assert.Single(files);
        Assert.Equal("мод: First", shared.FinalProvider);
        Assert.Equal(2, shared.Providers.Count);
        Assert.True(shared.HasConflict);
    }

    [Fact]
    public async Task BuildFinalTreeAsyncAddsProfileOverwriteAtHighestPriority()
    {
        CreateFile("game", "shared.ltx");
        var workspace = Path.Combine(_root, "profile-workspace");
        var overwrite = Path.Combine(workspace, "userdata", "overwrite", "shared.ltx");
        Directory.CreateDirectory(Path.GetDirectoryName(overwrite)!);
        File.WriteAllText(overwrite, "profile");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game") };
        var plan = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, workspace);

        var files = await FileLayerExplorerService.BuildFinalTreeAsync(plan, workspace);

        var shared = Assert.Single(files);
        Assert.Equal("профильный overwrite", shared.FinalProvider);
        Assert.Equal(["базовая игра", "профильный overwrite"], shared.Providers);
    }

    private string CreateFile(string folder, string relativePath)
    {
        var path = Path.Combine(_root, folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, folder);
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
