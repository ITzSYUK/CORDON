using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LaunchPreflightServiceTests : IDisposable
{
    private const string ValidFsgame = "$app_data_root$ = true | false | $fs_root$ | appdata";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnalyzeAsyncFindsFinalExecutableFromLastEnabledMod()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        var patchExecutable = CreateFile("patch/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(_root, "patch"), Order = 1 });

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Итоговый бинарник" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains(patchExecutable));
    }

    [Fact]
    public async Task AnalyzeAsyncCreatesLaunchPlanPreviewFromFileLayers()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("layered-game/fsgame.ltx", ValidFsgame);
        CreateFile("layered-game/bin/xr_3da.exe");
        var patchExecutable = CreateFile("layered-patch/bin_x64/xrEngine.exe");
        var profile = new ModProfile
        {
            Name = "Layered launch",
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin\missing.exe",
            LaunchArguments = "  -nointro  "
        };
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(_root, "layered-patch"), Order = 1 });

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.NotNull(report.LaunchPlan);
        Assert.NotNull(report.OverlayManifest);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, report.LaunchPlan.BackendKind);
        Assert.Equal("-nointro", report.LaunchPlan.Arguments);
        Assert.Equal(report.LaunchPlan.ExecutablePath, report.OverlayManifest.LaunchPlan?.ExecutablePath);
        Assert.EndsWith(Path.Combine("current", "bin_x64", "xrEngine.exe"), report.LaunchPlan.ExecutablePath);
        Assert.EndsWith("current", report.LaunchPlan.WorkingDirectory);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Итоговый бинарник" &&
                     check.Status == ProfileHealthStatus.Warning &&
                     check.Details.Contains(patchExecutable));
    }

    [Fact]
    public async Task AnalyzeAsyncUsesPinnedExecutableSource()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("game-pinned/fsgame.ltx", ValidFsgame);
        CreateFile("game-pinned/bin/xr_3da.exe");
        var mainExecutable = CreateFile("main/bin_x64/xrEngine.exe");
        CreateFile("patch/bin_x64/xrEngine.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            ExecutableSourcePath = Path.Combine(_root, "main")
        };
        profile.Mods.Add(new ModEntry { Name = "Main", SourcePath = Path.Combine(_root, "main"), Order = 1 });
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(_root, "patch"), Order = 2 });

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Итоговый бинарник" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains(mainExecutable) &&
                     check.Details.Contains("Выбран вручную"));
    }

    [Fact]
    public async Task AnalyzeAsyncReportsFsgameFromHighestPriorityLayer()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = Path.Combine(_root, "game-layered");
        CreateFile("game-layered/fsgame.ltx", ValidFsgame);
        CreateFile("game-layered/bin/xr_3da.exe");
        CreateFile("main/fsgame.ltx", ValidFsgame);
        var patchFsgame = CreateFile("patch/fsgame.ltx", ValidFsgame);
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(_root, "patch"), Order = 2 });
        profile.Mods.Add(new ModEntry { Name = "Main", SourcePath = Path.Combine(_root, "main"), Order = 1 });

        var report = await service.AnalyzeAsync(profile);

        Assert.Contains(
            report.Checks,
            check => check.Title == "fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details == patchFsgame);
    }

    [Fact]
    public async Task AnalyzeAsyncReportsManuallySelectedFsgame()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new LaunchPreflightService(new ProfileManager(paths, new WorkspaceBuilder(paths)));
        var game = Path.Combine(_root, "game-manual-fsgame");
        var selected = CreateFile(
            "game-manual-fsgame/fsgame_coc.ltx",
            "$app_data_root$ = true | false | $fs_root$ | _appdata_\\");
        var executable = CreateFile("manual-fsgame/_bin/xrEngine.exe");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"_bin\xrEngine.exe",
            FsgameSourcePath = selected
        };
        profile.Mods.Add(new ModEntry { Name = "Manual", SourcePath = Path.Combine(_root, "manual-fsgame"), Order = 1 });

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Checks, check => check.Title == "Базовая игра" && check.Status == ProfileHealthStatus.Healthy);
        Assert.Contains(report.Checks, check => check.Title == "Итоговый бинарник" && check.Details.Contains(executable));
        Assert.Contains(report.Checks, check => check.Title == "fsgame.ltx" && check.Details == selected);
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsManualFsgameWithoutAppDataRoot()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new LaunchPreflightService(new ProfileManager(paths, new WorkspaceBuilder(paths)));
        var game = Path.Combine(_root, "invalid-manual-fsgame");
        var selected = CreateFile("invalid-manual-fsgame/fsgame_coc.ltx");
        CreateFile("invalid-manual-fsgame/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            FsgameSourcePath = selected
        };

        var report = await service.AnalyzeAsync(profile);

        Assert.False(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Error &&
                     check.Details.Contains("$app_data_root$"));
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsAutomaticFsgameWithoutAppDataRoot()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new LaunchPreflightService(new ProfileManager(paths, new WorkspaceBuilder(paths)));
        var game = CreateFile("invalid-automatic-fsgame/fsgame.ltx", "invalid");
        CreateFile("invalid-automatic-fsgame/bin/xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = Path.GetDirectoryName(game)! };

        var report = await service.AnalyzeAsync(profile);

        Assert.False(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Error &&
                     check.Details.Contains("$app_data_root$"));
    }

    [Fact]
    public async Task AnalyzeAsyncWarnsAndFallsBackWhenRequestedExecutableIsMissing()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin\missing.exe"
        };

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Итоговый бинарник" &&
                     check.Status == ProfileHealthStatus.Warning &&
                     check.Details.Contains(@"bin\missing.exe") &&
                     check.Details.Contains(@"bin\xr_3da.exe"));
    }

    [Fact]
    public async Task AnalyzeAsyncBlocksWhenNoExecutableCanBeDetected()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("broken-game/fsgame.ltx", ValidFsgame);
        var profile = new ModProfile
        {
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin\missing.exe"
        };

        var report = await service.AnalyzeAsync(profile);

        Assert.False(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Итоговый бинарник" && check.Status == ProfileHealthStatus.Error);
    }

    [Fact]
    public async Task AnalyzeAsyncWarnsWhenEnabledModFolderIsEmpty()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder));
        var game = CreateFile("game-empty-mod/fsgame.ltx", ValidFsgame);
        CreateFile("game-empty-mod/bin/xr_3da.exe");
        var emptyMod = Path.Combine(_root, "empty-mod");
        Directory.CreateDirectory(emptyMod);
        var profile = new ModProfile
        {
            GameInstallPath = Path.GetDirectoryName(game)!,
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Empty", SourcePath = emptyMod, Order = 1 });

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Мод пуст: Empty" && check.Status == ProfileHealthStatus.Warning);
    }

    [Fact]
    public async Task AnalyzeAsyncUsesVirtualFileSystemPreviewAndValidatesRuntime()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var runtimeDirectory = Path.Combine(_root, "usvfs-runtime");
        CreateUsvfsRuntimeFiles(runtimeDirectory);
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder),
            runtimeDirectory);
        var game = Path.Combine(_root, "usvfs-game");
        CreateFile("usvfs-game/fsgame.ltx", ValidFsgame);
        var executable = Path.Combine(game, "bin_x64", "xrEngine.exe");
        CopyExecutable(executable, WindowsExecutableArchitecture.X64);
        var profile = new ModProfile
        {
            Name = "USVFS",
            GameInstallPath = game,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };

        var report = await service.AnalyzeAsync(profile);

        Assert.True(report.CanLaunch);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, report.LaunchPlan?.BackendKind);
        Assert.Equal(executable, report.LaunchPlan?.ExecutablePath);
        Assert.Equal(report.LaunchPlan?.BackendKind, report.OverlayManifest?.LaunchPlan?.BackendKind);
        Assert.Equal(report.LaunchPlan?.ExecutablePath, report.OverlayManifest?.LaunchPlan?.ExecutablePath);
        Assert.Equal(report.LaunchPlan?.WorkingDirectory, report.OverlayManifest?.LaunchPlan?.WorkingDirectory);
        Assert.Contains(
            report.Checks,
            check => check.Title == "USVFS runtime" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains("x64"));
    }

    [Fact]
    public async Task AnalyzeAsyncBlocksUsvfsWhenRuntimeBundleIsIncomplete()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var runtimeDirectory = Path.Combine(_root, "incomplete-usvfs-runtime");
        CreateUsvfsRuntimeFiles(runtimeDirectory);
        File.Delete(Path.Combine(runtimeDirectory, UsvfsRuntimeFiles.X86ProxyFileName));
        var service = new LaunchPreflightService(
            new ProfileManager(paths, builder),
            runtimeDirectory);
        var game = Path.Combine(_root, "incomplete-usvfs-game");
        CreateFile("incomplete-usvfs-game/fsgame.ltx", ValidFsgame);
        CopyExecutable(
            Path.Combine(game, "bin_x64", "xrEngine.exe"),
            WindowsExecutableArchitecture.X64);
        var profile = new ModProfile
        {
            Name = "Incomplete USVFS",
            GameInstallPath = game,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };

        var report = await service.AnalyzeAsync(profile);

        Assert.False(report.CanLaunch);
        Assert.Contains(
            report.Checks,
            check => check.Title == "USVFS runtime" &&
                     check.Status == ProfileHealthStatus.Error &&
                     check.Details.Contains(UsvfsRuntimeFiles.X86ProxyFileName));
    }

    private string CreateFile(string relativePath, string contents = "test")
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void CreateUsvfsRuntimeFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        CopyExecutable(
            Path.Combine(directory, UsvfsRuntimeFiles.ControllerDllFileName),
            WindowsExecutableArchitecture.X64);
        CopyExecutable(
            Path.Combine(directory, UsvfsRuntimeFiles.X64ProxyFileName),
            WindowsExecutableArchitecture.X64);
        CopyExecutable(
            Path.Combine(directory, UsvfsRuntimeFiles.X86DllFileName),
            WindowsExecutableArchitecture.X86);
        CopyExecutable(
            Path.Combine(directory, UsvfsRuntimeFiles.X86ProxyFileName),
            WindowsExecutableArchitecture.X86);
        CopyExecutable(
            Path.Combine(directory, UsvfsRuntimeFiles.X86HostFileName),
            WindowsExecutableArchitecture.X86);
    }

    private static void CopyExecutable(string destination, WindowsExecutableArchitecture architecture)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDirectory = architecture == WindowsExecutableArchitecture.X86 ? "SysWOW64" : "System32";
        var source = Path.Combine(windows, systemDirectory, "cmd.exe");
        Assert.True(File.Exists(source));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
