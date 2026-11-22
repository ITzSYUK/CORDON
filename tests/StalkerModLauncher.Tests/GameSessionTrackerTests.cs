using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class GameSessionTrackerTests
{
    [Theory]
    [InlineData(LaunchBackendKind.LinkedWorkspace)]
    [InlineData(LaunchBackendKind.Standalone)]
    public async Task DirectProfileStaysActiveAfterLauncherExits(LaunchBackendKind backend)
    {
        var fixture = Directory.CreateTempSubdirectory("CordonGameSession-").FullName;
        var game = Path.Combine(fixture, "game");
        var executable = Path.Combine(game, "launcher.exe");
        ProfileLaunchHandle? launch = null;
        try
        {
            Directory.CreateDirectory(game);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
            File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var profile = new ModProfile
            {
                GameInstallPath = game,
                ExecutableRelativePath = "launcher.exe",
                IsStandalone = backend == LaunchBackendKind.Standalone,
                LaunchBackendKind = backend,
                LaunchArguments = $"/d /c start \"\" /b /d \"{Environment.SystemDirectory}\" \"{powershell}\" -NoProfile -Command \"Start-Sleep -Seconds 30\""
            };
            if (profile.IsStandalone)
                profile.Mods.Add(new ModEntry { SourcePath = game });

            var paths = new AppPaths(Path.Combine(fixture, "config"), Path.Combine(fixture, "workspaces"), false);
            var builder = new WorkspaceBuilder(paths);
            var launcher = new ProfileLauncher(
                [new LinkedWorkspaceLaunchBackend(builder), new StandaloneLaunchBackend()],
                profileManager: new ProfileManager(paths, builder));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            launch = await launcher.LaunchAsync(game, profile, new Progress<string>(), timeout.Token);
            await launch.Process.WaitForExitAsync(timeout.Token);
            var rootId = launch.ProcessId;

            using var tracker = new GameSessionTracker();
            var session = tracker.TrackAsync(launch.Process, "Fixture", false, launch.Completion);
            Assert.False(session.IsCompleted);
            var active = launch.GetActiveProcessIds();
            Assert.NotEmpty(active);
            Assert.DoesNotContain(rootId, active);
            using var child = Process.GetProcessById(active[0]);
            Assert.True(launch.TryTerminate());
            Assert.Equal(0, (await session.WaitAsync(timeout.Token)).ExitCode);
            await child.WaitForExitAsync(timeout.Token);
            Assert.Empty(launch.GetActiveProcessIds());
        }
        finally
        {
            launch?.TryTerminate();
            if (launch?.Completion is { } completion)
                await completion.WaitAsync(TimeSpan.FromSeconds(5));
            launch?.Process.Dispose();
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public async Task ElevatedHelperUsesNamedJobAndReturnsGameExitCode()
    {
        var name = $@"Local\CORDON-Game-{Guid.NewGuid():N}";
        using var owner = WindowsGameJob.Create(name);
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CORDON.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ProcessLauncher.ElevatedLaunchArgument);
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        startInfo.ArgumentList.Add("/d /c exit 23");
        startInfo.ArgumentList.Add(Environment.SystemDirectory);
        using var helper = Process.Start(startInfo)!;
        try
        {
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(23, helper.ExitCode);
        }
        finally
        {
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void CreateResultRecordsSessionsAtLeastFiveSecondsLong()
    {
        var started = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

        var result = GameSessionTracker.CreateResult(started, started.AddSeconds(5), 7);

        Assert.True(result.ShouldRecord);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(started, result.StartedAtUtc);
    }

    [Fact]
    public void CreateResultIgnoresVeryShortSessions()
    {
        var started = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

        var result = GameSessionTracker.CreateResult(started, started.AddSeconds(4));

        Assert.False(result.ShouldRecord);
    }

    [Fact]
    public void CreateResultClampsNegativeDuration()
    {
        var started = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

        var result = GameSessionTracker.CreateResult(started, started.AddSeconds(-1));

        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.False(result.ShouldRecord);
    }
}
