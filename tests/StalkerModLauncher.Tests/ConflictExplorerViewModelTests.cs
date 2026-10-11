using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ConflictExplorerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", "ConflictExplorer", Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData(LaunchBackendKind.LinkedWorkspace, true)]
    [InlineData(LaunchBackendKind.LinkedWorkspace, false)]
    [InlineData(LaunchBackendKind.VirtualFileSystem, true)]
    [InlineData(LaunchBackendKind.VirtualFileSystem, false)]
    public async Task FinalTreeUsesPinnedExecutableFromLaunchPlan(LaunchBackendKind backend, bool pinBaseGame)
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var lower = CreateMod("lower-engine", 1);
        var higher = CreateMod("higher-engine", 2);
        const string relative = @"bin\xr_3da.exe";
        CreateFile(game, relative);
        CreateFile(lower.SourcePath, relative);
        CreateFile(higher.SourcePath, relative);
        var profile = new ModProfile
        {
            GameInstallPath = game,
            WorkspacePath = Path.Combine(_root, "workspace"),
            LaunchBackendKind = backend,
            ExecutableSourcePath = pinBaseGame ? game : lower.SourcePath
        };
        profile.Mods.Add(lower);
        profile.Mods.Add(higher);
        var plan = FileLayerPlan.CreateLinkedWorkspace(game, profile, profile.WorkspacePath);
        var launch = backend == LaunchBackendKind.VirtualFileSystem
            ? ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, plan)
            : ProfileLaunchPlanResolver.PreviewLinkedWorkspace(profile, plan, profile.WorkspacePath);
        Assert.Equal(Path.Combine(profile.ExecutableSourcePath, relative), launch.Executable?.FullPath);
        using var viewModel = new ConflictExplorerViewModel(
            profile, lower, new ModConflictAnalyzer(), new DialogService(), () => Task.CompletedTask, () => { });

        await viewModel.RefreshAsync();

        var executable = Assert.Single(viewModel.FinalFiles);
        Assert.Equal(launch.Executable!.SourceName, executable.FinalProvider);
        Assert.Equal(executable.FinalProvider, executable.Providers[^1]);
        Assert.Equal(3, executable.Providers.Count);
    }

    [Fact]
    public async Task PinnedExecutableWithSingleProviderDoesNotCreateFalseConflict()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        CreateFile(game, @"bin\xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = game, ExecutableSourcePath = game };
        using var viewModel = new ConflictExplorerViewModel(
            profile, null, new ModConflictAnalyzer(), new DialogService(), () => Task.CompletedTask, () => { });

        await viewModel.RefreshAsync();

        var executable = Assert.Single(viewModel.FinalFiles);
        Assert.Equal(Strings.Layer_BaseGame, executable.FinalProvider);
        Assert.Single(executable.Providers);
        Assert.False(executable.HasConflict);
    }

    [Fact]
    public async Task RefreshImmediatelyUpdatesNestedFileConflicts()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var lower = CreateMod("lower-refresh", 1);
        var higher = CreateMod("higher-refresh", 2);
        const string existing = @"gamedata\configs\existing.ltx";
        const string added = @"gamedata\configs\added.ltx";
        CreateFile(lower.SourcePath, existing);
        CreateFile(higher.SourcePath, existing);
        var profile = new ModProfile { GameInstallPath = game };
        profile.Mods.Add(lower);
        profile.Mods.Add(higher);
        using var viewModel = new ConflictExplorerViewModel(
            profile, higher, new ModConflictAnalyzer(), new DialogService(), () => Task.CompletedTask, () => { });
        await viewModel.RefreshAsync();
        Assert.Single(viewModel.WinningFiles);
        var lowerRootTime = Directory.GetLastWriteTimeUtc(lower.SourcePath);
        var higherRootTime = Directory.GetLastWriteTimeUtc(higher.SourcePath);
        CreateFile(lower.SourcePath, added);
        CreateFile(higher.SourcePath, added);
        Assert.Equal(lowerRootTime, Directory.GetLastWriteTimeUtc(lower.SourcePath));
        Assert.Equal(higherRootTime, Directory.GetLastWriteTimeUtc(higher.SourcePath));

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.WinningFiles.Count);
        Assert.Contains(viewModel.WinningFiles, file => file.RelativePath == added);
        Assert.True(viewModel.FinalFiles.Single(file => file.RelativePath == added).HasConflict);
        File.Delete(Path.Combine(lower.SourcePath, added));
        File.Delete(Path.Combine(higher.SourcePath, added));

        await viewModel.RefreshAsync();

        Assert.Equal(existing, Assert.Single(viewModel.WinningFiles).RelativePath);
        Assert.DoesNotContain(viewModel.FinalFiles, file => file.RelativePath == added);
    }

    [Fact]
    public void TabSelectionFlagsAndCommandFollowSelectedTabIndex()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var profile = new ModProfile { GameInstallPath = game, WorkspacePath = Path.Combine(_root, "workspace") };

        using var viewModel = new ConflictExplorerViewModel(
            profile,
            null,
            new ModConflictAnalyzer(),
            new DialogService(),
            () => Task.CompletedTask,
            () => { });

        Assert.Equal(1, viewModel.SelectedTabIndex);
        Assert.False(viewModel.IsModConflictsTab);
        Assert.True(viewModel.IsFinalTreeTab);

        viewModel.SelectTabCommand.Execute("0");
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.True(viewModel.IsModConflictsTab);
        Assert.False(viewModel.IsFinalTreeTab);

        viewModel.SelectTabCommand.Execute(1);
        Assert.Equal(1, viewModel.SelectedTabIndex);

        viewModel.SelectTabCommand.Execute("nope");
        Assert.Equal(1, viewModel.SelectedTabIndex);

        viewModel.SelectTabCommand.Execute(5);
        Assert.Equal(1, viewModel.SelectedTabIndex);
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
