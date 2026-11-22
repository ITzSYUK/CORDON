using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LinkedWorkspaceLaunchBackendTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherBackendTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareAsyncBuildsWorkspaceAndReturnsLaunchPlan()
    {
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mods", "patch");
        var overwrite = Path.Combine(_root, "overwrite");
        var workspaceRoot = Path.Combine(_root, "workspaces");
        var backend = new LinkedWorkspaceLaunchBackend(new WorkspaceBuilder(new AppPaths(
            Path.Combine(_root, "config"),
            workspaceRoot,
            preferGameDriveWorkspace: false)));
        var profile = new ModProfile
        {
            Name = "Backend profile",
            Mo2OverwritePath = overwrite,
            ExecutableRelativePath = @"bin\xr_3da.exe",
            LaunchArguments = "  -nointro -dbg  "
        };
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = mod, IsEnabled = true, Order = 1 });
        CreateFile(game, "bin/xr_3da.exe", "base executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(mod, "bin/xr_3da.exe", "mod executable");
        CreateFile(mod, "gamedata/example.ltx", "mod");
        CreateFile(overwrite, "gamedata/example.ltx", "overwrite");

        var fileLayerPlan = FileLayerPlan.CreateForProfile(game, profile, Path.Combine(workspaceRoot, $"profile-{profile.Id}"));
        var launchContext = new ProfileLaunchBackendContext(game, profile, fileLayerPlan);

        var plan = await backend.PrepareAsync(launchContext, new ProgressLog());

        Assert.Equal(LaunchBackendKind.LinkedWorkspace, plan.BackendKind);
        Assert.Equal("-nointro -dbg", plan.Arguments);
        Assert.EndsWith(Path.Combine("current", "bin", "xr_3da.exe"), plan.ExecutablePath);
        Assert.EndsWith("current", plan.WorkingDirectory);
        Assert.EndsWith($"profile-{profile.Id}", profile.WorkspacePath);
        Assert.Equal(@"bin\xr_3da.exe", profile.ExecutableRelativePath);
        Assert.Empty(profile.WorkingDirectoryRelative);
        Assert.Equal("mod executable", File.ReadAllText(plan.ExecutablePath));
        Assert.Equal("overwrite", File.ReadAllText(Path.Combine(plan.WorkingDirectory, "gamedata", "example.ltx")));
        Assert.Equal("base executable", File.ReadAllText(Path.Combine(game, "bin", "xr_3da.exe")));
    }

    [Fact]
    public async Task ProfileLauncherStartsExecutableInLocalDataWorkspace()
    {
        var game = Path.Combine(_root, "game");
        var paths = new AppPaths(Path.Combine(_root, "launcher"));
        var builder = new WorkspaceBuilder(paths);
        var manager = new ProfileManager(paths, builder);
        var launcher = new ProfileLauncher([new LinkedWorkspaceLaunchBackend(builder)], profileManager: manager);
        var profile = new ModProfile
        {
            GameInstallPath = game,
            LaunchBackendKind = LaunchBackendKind.LinkedWorkspace,
            LaunchArguments = "/d /c echo workspace>ran.txt"
        };
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        var executable = Path.Combine(game, profile.ExecutableRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var launched = await launcher.LaunchAsync(game, profile, new ProgressLog(), timeout.Token);
        using var process = launched.Process;
        var exitCode = await launched.Completion!.WaitAsync(timeout.Token);

        var current = Path.Combine(paths.ExecutableDirectory, "Data", "CordonModLauncher", "Workspaces", $"profile-{profile.Id}", "current");
        Assert.Equal(0, exitCode);
        Assert.Equal("workspace", (await File.ReadAllTextAsync(Path.Combine(current, "ran.txt"), timeout.Token)).Trim());
        Assert.False(File.Exists(Path.Combine(game, "ran.txt")));
    }

    [Theory]
    [InlineData(true, false, false, "", "root")]
    [InlineData(false, false, false, "bin", "mod")]
    [InlineData(false, true, false, "bin", "base")]
    [InlineData(false, false, true, "bin", "mod")]
    public async Task ReadinessPreviewAndBuildUseTheSameFsgame(
        bool hasRootFile, bool excluded, bool useAnomalyLauncher, string workingDirectory, string sourceMarker)
    {
        const string config = "$app_data_root$ = true | false | appdata";
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mod");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = new ModProfile { GameInstallPath = game, LaunchBackendKind = LaunchBackendKind.LinkedWorkspace };
        if (useAnomalyLauncher)
        {
            profile.ExecutableRelativePath = "AnomalyLauncher.exe";
            CreateFile(game, "appdata/fsgame.ltx", config + "\n$test$ = wrong");
        }

        profile.Mods.Add(new ModEntry { SourcePath = mod, Order = 1, ExcludedFiles = excluded ? [@"bin\fsgame.ltx"] : [] });
        CreateFile(game, profile.ExecutableRelativePath, "game executable");
        CreateFile(game, "bin/fsgame.ltx", config + "\n$test$ = base");
        CreateFile(mod, "bin/fsgame.ltx", config + "\n$test$ = mod");
        if (hasRootFile)
            CreateFile(game, "fsgame.ltx", config + "\n$test$ = root");

        Assert.True(ProfileReadinessService.Validate(profile).IsValid);
        var preview = await new LaunchPreflightService(new ProfileManager(paths, builder)).AnalyzeAsync(profile);
        Assert.True(preview.CanLaunch, preview.ToErrorMessage());

        var result = await builder.BuildAsync(game, profile, new ProgressLog());
        try
        {
            var effectiveDirectory = Path.Combine(result.WorkspaceRoot, workingDirectory);
            Assert.Equal(effectiveDirectory, preview.LaunchPlan!.WorkingDirectory);
            Assert.Equal(workingDirectory, result.WorkingDirectoryRelative);
            var prepared = File.ReadAllText(Path.Combine(effectiveDirectory, "fsgame.ltx"));
            Assert.Contains($"$test$ = {sourceMarker}", prepared, StringComparison.Ordinal);
        }
        finally
        {
            if (useAnomalyLauncher) AnomalyAppDataAlias.Detach(result.WorkspaceRoot);
        }
    }

    private static void CreateFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        for (var attempt = 0; Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is (IOException or UnauthorizedAccessException))
            {
                // Windows can briefly hold the executable image after its process exits.
                await Task.Delay(100);
            }
        }
    }

    private sealed class ProgressLog : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value)
        {
            Messages.Add(value);
        }
    }
}
