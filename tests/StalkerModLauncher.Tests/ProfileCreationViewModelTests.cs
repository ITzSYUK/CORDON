using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileCreationViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void NextCommandAutoDetectsExecutableFromHighestPriorityMod()
    {
        var game = CreateDirectory("game");
        CreateFile(game, "bin/xr_3da.exe");
        var mainMod = CreateDirectory("main-mod");
        CreateFile(mainMod, "bin_x64/xrEngine.exe");
        var patch = CreateDirectory("patch");
        CreateFile(patch, "bin_x64/xrEngine.exe");
        var viewModel = new ProfileCreationViewModel()
        {
            Name = "Ликвидация",
            GamePath = game
        };
        ModProfile? createdProfile = null;
        viewModel.Completed += (_, profile) => createdProfile = profile;
        viewModel.Mods.Add(new ModEntry { Name = "main", SourcePath = mainMod, Order = 1 });
        viewModel.Mods.Add(new ModEntry { Name = "patch", SourcePath = patch, Order = 2 });

        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsStepThree);
        Assert.Equal(@"bin_x64\xrEngine.exe", viewModel.ExecutableRelativePath);
        Assert.Contains("мод: patch", viewModel.ExecutableDetectionMessage);
        viewModel.FinishCommand.Execute(null);
        Assert.Equal(
            LaunchBackendKind.VirtualFileSystem,
            Assert.IsType<ModProfile>(createdProfile).LaunchBackendKind);
    }

    [Fact]
    public void FinishCommandBlocksProfileWhenExecutableIsMissing()
    {
        var game = CreateDirectory("game-without-exe");
        var viewModel = new ProfileCreationViewModel()
        {
            Name = "Без бинарника",
            GamePath = game
        };
        var completed = false;
        viewModel.Completed += (_, _) => completed = true;

        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        viewModel.FinishCommand.Execute(null);

        Assert.False(completed);
        Assert.Contains("Файл запуска не найден", viewModel.Message);
    }

    [Fact]
    public void AddDroppedModsAddsDirectoriesInDropOrderAndSkipsDuplicatesAndFiles()
    {
        var first = CreateDirectory("first-mod");
        var second = CreateDirectory("second-mod");
        var file = Path.Combine(_root, "not-a-directory.txt");
        File.WriteAllText(file, "test");
        var viewModel = new ProfileCreationViewModel();

        viewModel.AddDroppedMods([first, file, second, first]);

        Assert.Collection(
            viewModel.Mods,
            mod =>
            {
                Assert.Equal("first-mod", mod.Name);
                Assert.Equal(1, mod.Order);
            },
            mod =>
            {
                Assert.Equal("second-mod", mod.Name);
                Assert.Equal(2, mod.Order);
            });
    }

    [Fact]
    public void DroppedPathsSetGameAndStandaloneSourcesForMatchingProfileType()
    {
        var game = CreateDirectory("game-drop");
        var standalone = CreateDirectory("standalone-drop");
        var viewModel = new ProfileCreationViewModel();

        viewModel.SetDroppedGamePath(game);
        viewModel.IsStandalone = true;
        viewModel.SetDroppedStandalonePath(standalone);

        Assert.Equal(Path.GetFullPath(game), viewModel.GamePath);
        var mod = Assert.Single(viewModel.Mods);
        Assert.Equal(Path.GetFullPath(standalone), mod.SourcePath);
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
