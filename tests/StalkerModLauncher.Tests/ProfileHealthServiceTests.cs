using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));
    private readonly ProfileHealthService _service;

    public ProfileHealthServiceTests()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var manager = new ProfileManager(paths, new FakeWorkspaceManager());
        _service = new ProfileHealthService(manager);
    }

    [Fact]
    public async Task AnalyzeAsyncReportsReadyOverlayProfile()
    {
        var game = CreateGame();
        var mod = CreateFile("mod/gamedata/test.ltx");
        var workspace = Path.Combine(_root, "workspace");
        CreateFileAtPath(Path.Combine(workspace, ".stalker-launcher-workspace"));
        CreateFileAtPath(Path.Combine(workspace, "build-manifest.json"));
        CreateFileAtPath(Path.Combine(workspace, "current", "bin", "xr_3da.exe"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "test.sav"));
        var profile = new ModProfile { GameInstallPath = game, WorkspacePath = workspace };
        profile.Mods.Add(new ModEntry { Name = "Mod", SourcePath = Path.GetDirectoryName(Path.GetDirectoryName(mod))!, Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.True(report.IsReady);
        Assert.Equal(0, report.ErrorCount);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Сохранения" &&
                     check.Details.StartsWith("Файлов: 1.", StringComparison.Ordinal));
        Assert.Contains(
            report.Checks,
            check => check.Title == "Источник fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains("Найден автоматически", StringComparison.Ordinal) &&
                     check.Details.Contains(Path.Combine(game, "fsgame.ltx"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncReportsManuallySelectedFsgameSource()
    {
        var game = CreateGame();
        var mod = Path.Combine(_root, "manual-fsgame");
        var selected = CreateFileAtPath(
            Path.Combine(mod, "fsgame_coc.ltx"),
            "$app_data_root$ = true | false | _appdata_\\");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            FsgameSourcePath = selected
        };
        profile.Mods.Add(new ModEntry { Name = "CoC", SourcePath = mod, Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.Contains(
            report.Checks,
            check => check.Title == "Источник fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains("Выбран вручную", StringComparison.Ordinal) &&
                     check.Details.Contains(selected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncWarnsWhenFsgameSourceIsNotFound()
    {
        var game = Path.Combine(_root, "game-without-fsgame");
        CreateFileAtPath(Path.Combine(game, "bin", "xr_3da.exe"));
        var profile = new ModProfile { GameInstallPath = game };

        var report = await _service.AnalyzeAsync(profile);

        Assert.Contains(
            report.Checks,
            check => check.Title == "Источник fsgame.ltx" &&
                     check.Status == ProfileHealthStatus.Warning &&
                     check.Details.Contains("не найден автоматически", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncCountsScopSavesButNotTheirMetadataOrPreview()
    {
        var workspace = Path.Combine(_root, "ixray-workspace");
        CreateFileAtPath(Path.Combine(workspace, ".stalker-launcher-workspace"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "first.scop"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "first.scop_data"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "first.dds"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "second.SCOP"));
        CreateFileAtPath(Path.Combine(workspace, "userdata", "savedgames", "unrelated.txt"));
        var profile = new ModProfile
        {
            GameInstallPath = CreateGame(),
            WorkspacePath = workspace
        };

        var report = await _service.AnalyzeAsync(profile);

        Assert.Contains(
            report.Checks,
            check => check.Title == "Сохранения" &&
                     check.Details.StartsWith("Файлов: 2.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsOverlayProfileWithoutOwnGamePath()
    {
        _ = CreateGame();
        var profile = new ModProfile { GameInstallPath = string.Empty };

        var report = await _service.AnalyzeAsync(profile);

        Assert.False(report.IsReady);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Базовая игра" &&
                     check.Status == ProfileHealthStatus.Error &&
                     check.Details.Contains("Папка игры не выбрана."));
    }

    [Fact]
    public async Task AnalyzeAsyncReportsMissingModAndExecutable()
    {
        var profile = new ModProfile
        {
            GameInstallPath = CreateGame(),
            ExecutableRelativePath = @"bin\missing.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Missing", SourcePath = Path.Combine(_root, "missing"), Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, check => check.Title.Contains("Missing") && check.Status == ProfileHealthStatus.Error);
        Assert.Contains(report.Checks, check => check.Title == "Бинарник запуска" && check.Status == ProfileHealthStatus.Warning);
    }

    [Fact]
    public async Task AnalyzeAsyncReportsUnavailablePinnedExecutableAsError()
    {
        var game = CreateGame();
        var disabledMod = Path.Combine(_root, "engine-mod");
        CreateFileAtPath(Path.Combine(disabledMod, "bin", "AnomalyDX11.exe"));
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"bin\AnomalyDX11.exe",
            ExecutableSourcePath = disabledMod
        };
        profile.Mods.Add(new ModEntry
        {
            Name = "Engine mod",
            SourcePath = disabledMod,
            IsEnabled = false,
            Order = 1
        });

        var report = await _service.AnalyzeAsync(profile);

        Assert.Contains(
            report.Checks,
            check => check.Title == "Бинарник запуска" &&
                     check.Status == ProfileHealthStatus.Error &&
                     check.Details.Contains("недоступна или мод выключен", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncTreatsMissingDisabledModAsWarning()
    {
        var profile = new ModProfile { GameInstallPath = CreateGame() };
        profile.Mods.Add(new ModEntry
        {
            Name = "Disabled",
            SourcePath = Path.Combine(_root, "missing"),
            IsEnabled = false,
            Order = 1
        });

        var report = await _service.AnalyzeAsync(profile);

        Assert.True(report.IsReady);
        Assert.Contains(report.Checks, check => check.Title.Contains("Disabled") && check.Status == ProfileHealthStatus.Warning);
    }

    [Fact]
    public async Task AnalyzeAsyncFindsLatestLogAndCrashDump()
    {
        var modRoot = Path.Combine(_root, "standalone");
        CreateFileAtPath(Path.Combine(modRoot, "bin_x64", "xrEngine.exe"));
        var workspace = Path.Combine(_root, "workspace");
        var log = CreateFileAtPath(Path.Combine(workspace, "userdata", "logs", "xray.log"));
        var dump = CreateFileAtPath(Path.Combine(workspace, "userdata", "logs", "xray.mdmp"));
        var profile = new ModProfile
        {
            IsStandalone = true,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            WorkspacePath = workspace
        };
        profile.Mods.Add(new ModEntry { Name = "Standalone", SourcePath = modRoot, Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.Equal(log, report.LatestLogPath);
        Assert.Equal(dump, report.LatestCrashDumpPath);
        Assert.Equal(modRoot, report.ProfileFolderPath);
        Assert.Equal(2, report.WarningCount);
        Assert.Contains(Strings.Check_CrashDump, report.ToText("Standalone"));
    }

    [Fact]
    public async Task AnalyzeAsyncFindsStandaloneAppdataLogAndSaves()
    {
        var modRoot = Path.Combine(_root, "cpogsr");
        CreateFileAtPath(Path.Combine(modRoot, "bin_x64", "xrEngine.exe"));
        var log = CreateFileAtPath(Path.Combine(modRoot, "appdata", "logs", "xray.log"));
        var save = CreateFileAtPath(Path.Combine(modRoot, "appdata", "savedgames", "test.sav"));
        var profile = new ModProfile
        {
            IsStandalone = true,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "ОП ОГСР", SourcePath = modRoot, Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.Equal(log, report.LatestLogPath);
        Assert.Equal(Path.GetDirectoryName(save), report.SavedGamesPath);
        Assert.Contains(
            report.Checks,
            check => check.Title == "Сохранения" &&
                     check.Details.StartsWith("Файлов: 1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncUsesWorkspaceAsOverlayProfileFolder()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var profile = new ModProfile { GameInstallPath = CreateGame(), WorkspacePath = workspace };

        var report = await _service.AnalyzeAsync(profile);

        Assert.Equal(workspace, report.ProfileFolderPath);
    }

    [Fact]
    public async Task AnalyzeAsyncDescribesUsvfsWithoutRequiringCurrentCache()
    {
        var workspace = Path.Combine(_root, "usvfs-profile");
        CreateFileAtPath(Path.Combine(workspace, ".stalker-launcher-workspace"));
        var profile = new ModProfile
        {
            GameInstallPath = CreateGame(),
            WorkspacePath = workspace,
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem,
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };

        var report = await _service.AnalyzeAsync(profile);

        Assert.Equal(LaunchBackendKind.VirtualFileSystem, report.LaunchPlan?.BackendKind);
        Assert.Contains(
            report.Checks,
            check => check.Title == "USVFS" &&
                     check.Status == ProfileHealthStatus.Healthy &&
                     check.Details.Contains("current не используется"));
        Assert.DoesNotContain(report.Checks, check => check.Title == "Кэш workspace");
    }

    [Fact]
    public async Task AnalyzeAsyncReportsLastEnabledModAsExecutableSource()
    {
        var game = CreateGame();
        var mainMod = Path.Combine(_root, "liquidation");
        var patch = Path.Combine(_root, "liquidation-patch");
        var mainExecutable = CreateFileAtPath(Path.Combine(mainMod, "bin_x64", "xrEngine.exe"));
        var patchExecutable = CreateFileAtPath(Path.Combine(patch, "bin_x64", "xrEngine.exe"));
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Liquidation", SourcePath = mainMod, Order = 1 });
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = patch, Order = 2 });

        var report = await _service.AnalyzeAsync(profile);

        var executableCheck = Assert.Single(report.Checks, check => check.Title == "Бинарник запуска");
        Assert.DoesNotContain(mainExecutable, executableCheck.Details);
        Assert.Contains(patchExecutable, executableCheck.Details);
        Assert.Contains("мод: Patch", executableCheck.Details);
    }

    [Fact]
    public async Task AnalyzeAsyncCreatesLaunchPlanPreviewWithDetectedWorkingDirectory()
    {
        var game = CreateGame();
        var mod = Path.Combine(_root, "ogsr-mod");
        CreateFileAtPath(Path.Combine(mod, "bin_x64", "xrEngine.exe"));
        CreateFileAtPath(Path.Combine(mod, "bin_x64", "fsgame.ltx"));
        var profile = new ModProfile
        {
            Name = "OGSR",
            GameInstallPath = game,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            LaunchArguments = "  -nointro  "
        };
        profile.Mods.Add(new ModEntry { Name = "OGSR", SourcePath = mod, Order = 1 });

        var report = await _service.AnalyzeAsync(profile);

        Assert.True(report.IsReady);
        Assert.NotNull(report.LaunchPlan);
        Assert.NotNull(report.OverlayManifest);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, report.LaunchPlan.BackendKind);
        Assert.Equal("-nointro", report.LaunchPlan.Arguments);
        Assert.Equal(report.LaunchPlan.WorkingDirectory, report.OverlayManifest.LaunchPlan?.WorkingDirectory);
        Assert.EndsWith(Path.Combine("current", "bin_x64", "xrEngine.exe"), report.LaunchPlan.ExecutablePath);
        Assert.EndsWith(Path.Combine("current", "bin_x64"), report.LaunchPlan.WorkingDirectory);
    }

    [Fact]
    public async Task AnalyzeAsyncIgnoresDisabledLaterExecutableProvider()
    {
        var game = CreateGame();
        var mainMod = Path.Combine(_root, "main");
        var disabledPatch = Path.Combine(_root, "disabled-patch");
        var mainExecutable = CreateFileAtPath(Path.Combine(mainMod, "bin_x64", "xrEngine.exe"));
        CreateFileAtPath(Path.Combine(disabledPatch, "bin_x64", "xrEngine.exe"));
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Main", SourcePath = mainMod, Order = 1 });
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = disabledPatch, IsEnabled = false, Order = 2 });

        var report = await _service.AnalyzeAsync(profile);

        var executableCheck = Assert.Single(report.Checks, check => check.Title == "Бинарник запуска");
        Assert.Contains(mainExecutable, executableCheck.Details);
        Assert.DoesNotContain(disabledPatch, executableCheck.Details);
    }

    private string CreateGame()
    {
        var path = Path.Combine(_root, "game");
        CreateFileAtPath(
            Path.Combine(path, "fsgame.ltx"),
            "$app_data_root$ = true | false | _appdata_\\");
        CreateFileAtPath(Path.Combine(path, "bin", "xr_3da.exe"));
        return path;
    }

    private string CreateFile(string relativePath)
    {
        return CreateFileAtPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string CreateFileAtPath(string path, string contents = "test")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeWorkspaceManager : IProfileWorkspaceManager
    {
        public string EnsureProfileWorkspace(ModProfile profile, string gamePath, IProgress<string>? progress = null)
        {
            return string.IsNullOrWhiteSpace(profile.WorkspacePath)
                ? Path.Combine(Path.GetTempPath(), $"profile-{profile.Id}")
                : profile.WorkspacePath;
        }

        public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
        {
        }

        public void ClearProfileWorkspaceCache(ModProfile profile, string gamePath)
        {
        }
    }
}
