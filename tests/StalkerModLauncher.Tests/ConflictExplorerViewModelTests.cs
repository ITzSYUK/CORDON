using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ConflictExplorerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"conflict-explorer-{Guid.NewGuid():N}");

    [Fact]
    public async Task MixedConflictAppearsInWinningAndLosingLists()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var lower = CreateMod("lower", 1);
        var selected = CreateMod("selected", 2);
        var higher = CreateMod("higher", 3);
        const string relativePath = @"gamedata\configs\shared.ltx";
        CreateFile(lower.SourcePath, relativePath);
        CreateFile(selected.SourcePath, relativePath);
        CreateFile(higher.SourcePath, relativePath);

        var profile = new ModProfile { GameInstallPath = game, WorkspacePath = Path.Combine(_root, "workspace") };
        profile.Mods.Add(lower);
        profile.Mods.Add(selected);
        profile.Mods.Add(higher);

        using var viewModel = new ConflictExplorerViewModel(
            profile,
            selected,
            new ModConflictAnalyzer(),
            new DialogService(),
            () => Task.CompletedTask,
            () => { });
        await viewModel.RefreshAsync();

        Assert.Equal(relativePath, Assert.Single(viewModel.WinningFiles).RelativePath);
        Assert.Equal(relativePath, Assert.Single(viewModel.LosingFiles).RelativePath);
    }

    private ModEntry CreateMod(string name, int order) => new()
    {
        Name = name,
        SourcePath = Directory.CreateDirectory(Path.Combine(_root, name)).FullName,
        IsEnabled = true,
        Order = order
    };

    private static void CreateFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
