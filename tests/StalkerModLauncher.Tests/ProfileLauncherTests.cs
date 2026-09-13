using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileLauncherTests
{
    [Fact]
    public async Task LaunchAsyncUsesLinkedWorkspaceBackendByDefault()
    {
        using var process = Process.GetCurrentProcess();
        var linkedBackend = new RecordingLaunchBackend(LaunchBackendKind.LinkedWorkspace);
        var executor = new RecordingLaunchPlanExecutor(process);
        var launcher = new ProfileLauncher([linkedBackend], executor);
        var profile = new ModProfile();

        var launchedProcess = await launcher.LaunchAsync("game", profile, new Progress<string>());

        Assert.Same(process, launchedProcess.Process);
        Assert.Same(profile, linkedBackend.Profile);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, executor.Plan?.BackendKind);
    }

    [Fact]
    public async Task LaunchAsyncRejectsUnavailableVirtualFileSystemWithoutChangingProfile()
    {
        using var process = Process.GetCurrentProcess();
        var linkedBackend = new RecordingLaunchBackend(LaunchBackendKind.LinkedWorkspace);
        var executor = new RecordingLaunchPlanExecutor(process);
        var launcher = new ProfileLauncher([linkedBackend], executor);
        var profile = new ModProfile { LaunchBackendKind = LaunchBackendKind.VirtualFileSystem };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync("game", profile, new ListProgress()));

        Assert.Contains("usvfs_x64.dll", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, profile.LaunchBackendKind);
        Assert.Null(linkedBackend.Profile);
        Assert.Null(executor.Plan);
    }

    [Fact]
    public async Task LaunchAsyncReportsSelectedBackend()
    {
        using var process = Process.GetCurrentProcess();
        var launcher = new ProfileLauncher(
            [new RecordingLaunchBackend(LaunchBackendKind.LinkedWorkspace)],
            new RecordingLaunchPlanExecutor(process));
        var progress = new ListProgress();

        await launcher.LaunchAsync("game", new ModProfile(), progress);

        Assert.Contains("Режим запуска: Workspace.", progress.Messages);
        Assert.Contains("Запуск: C:\\Game\\LinkedWorkspace.exe", progress.Messages);
    }

    [Fact]
    public async Task LaunchAsyncReportsVirtualFileSystemFailureWithoutSilentFallback()
    {
        using var process = Process.GetCurrentProcess();
        var linkedBackend = new RecordingLaunchBackend(LaunchBackendKind.LinkedWorkspace);
        var launcher = new ProfileLauncher(
            [linkedBackend, new ThrowingLaunchBackend(LaunchBackendKind.VirtualFileSystem)],
            new RecordingLaunchPlanExecutor(process));
        var profile = new ModProfile { LaunchBackendKind = LaunchBackendKind.VirtualFileSystem };
        var progress = new ListProgress();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync("game", profile, progress));

        Assert.Equal("overlay setup failed", error.Message);
        Assert.Contains("Ошибка запуска USVFS: overlay setup failed", progress.Messages);
        Assert.Contains(
            progress.Messages,
            message => message.Contains("не переключён на Workspace автоматически", StringComparison.Ordinal));
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, profile.LaunchBackendKind);
        Assert.Null(linkedBackend.Profile);
    }

    [Fact]
    public async Task LaunchAsyncPassesFileLayerPlanAndOverlayManifestToBackend()
    {
        using var process = Process.GetCurrentProcess();
        var root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherProfileLauncherTests", Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(root, "game");
            var mod = Path.Combine(root, "mod");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(mod);
            var paths = new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "workspaces"), false);
            var profileManager = new ProfileManager(paths, new NoopWorkspaceManager());
            var linkedBackend = new RecordingLaunchBackend(LaunchBackendKind.LinkedWorkspace);
            var launcher = new ProfileLauncher(
                [linkedBackend],
                new RecordingLaunchPlanExecutor(process),
                profileManager);
            var profile = new ModProfile
            {
                Name = "Layered profile",
                GameInstallPath = game
            };
            profile.Mods.Add(new ModEntry { Id = "mod", Name = "Patch", SourcePath = mod, Order = 1 });

            await launcher.LaunchAsync(game, profile, new Progress<string>());

            Assert.False(string.IsNullOrWhiteSpace(profile.WorkspacePath));
            Assert.NotNull(linkedBackend.Context?.FileLayerPlan);
            Assert.NotNull(linkedBackend.Context?.OverlayManifest);
            Assert.Equal(
                profile.WorkspacePath,
                Path.GetFullPath(Path.Combine(linkedBackend.Context.OverlayManifest.WriteOverlayRoot, "..", "..")));
            Assert.Equal(["__base_game", "mod", "__userdata"], linkedBackend.Context.FileLayerPlan.Layers.Select(layer => layer.Id));
            Assert.Equal(3, linkedBackend.Context.OverlayManifest.Layers.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LaunchAsyncDisposesRuntimeLeaseWhenProcessStartFails()
    {
        var runtimeLease = new RecordingRuntimeLease();
        var backend = new RuntimeLeaseLaunchBackend(runtimeLease);
        var launcher = new ProfileLauncher(
            [backend],
            new ThrowingLaunchPlanExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync("game", new ModProfile(), new Progress<string>()));

        Assert.True(runtimeLease.IsDisposed);
    }

    [Fact]
    public async Task LaunchAsyncKeepsRuntimeLeaseUntilBackendSessionCompletes()
    {
        using var process = Process.GetCurrentProcess();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeLease = new RecordingRuntimeLease();
        var launcher = new ProfileLauncher(
            [new RuntimeLeaseLaunchBackend(runtimeLease, completion.Task)],
            new RecordingLaunchPlanExecutor(process));

        var launch = await launcher.LaunchAsync("game", new ModProfile(), new Progress<string>());

        Assert.False(runtimeLease.IsDisposed);
        completion.SetResult(17);
        Assert.Equal(17, await launch.Completion!);
        Assert.True(runtimeLease.IsDisposed);
    }

    private sealed class RecordingLaunchBackend(LaunchBackendKind kind) : IProfileLaunchBackend
    {
        public LaunchBackendKind Kind { get; } = kind;
        public string? GamePath { get; private set; }
        public ModProfile? Profile { get; private set; }
        public ProfileLaunchBackendContext? Context { get; private set; }

        public Task<LaunchPlan> PrepareAsync(
            ProfileLaunchBackendContext context,
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            Context = context;
            GamePath = context.GamePath;
            Profile = context.Profile;
            return Task.FromResult(new LaunchPlan(
                Kind,
                $"C:\\Game\\{Kind}.exe",
                "-nointro",
                "C:\\Game"));
        }
    }

    private sealed class ThrowingLaunchBackend(LaunchBackendKind kind) : IProfileLaunchBackend
    {
        public LaunchBackendKind Kind { get; } = kind;

        public Task<LaunchPlan> PrepareAsync(
            ProfileLaunchBackendContext context,
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("overlay setup failed");
        }
    }

    private sealed class RecordingLaunchPlanExecutor(Process process) : ILaunchPlanExecutor
    {
        public LaunchPlan? Plan { get; private set; }

        public Process Start(LaunchPlan plan, IProgress<string>? progress = null)
        {
            Plan = plan;
            return process;
        }
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value)
        {
            Messages.Add(value);
        }
    }

    private sealed class RuntimeLeaseLaunchBackend(
        IAsyncDisposable runtimeLease,
        Task<int>? completion = null) : IProfileLaunchBackend
    {
        public LaunchBackendKind Kind => LaunchBackendKind.LinkedWorkspace;

        public Task<LaunchPlan> PrepareAsync(
            ProfileLaunchBackendContext context,
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LaunchPlan(
                Kind,
                "C:\\Game\\LinkedWorkspace.exe",
                "-nointro",
                "C:\\Game",
                runtimeLease,
                runtimeCompletion: completion is null ? null : () => completion));
        }
    }

    private sealed class ThrowingLaunchPlanExecutor : ILaunchPlanExecutor
    {
        public Process Start(LaunchPlan plan, IProgress<string>? progress = null)
        {
            throw new InvalidOperationException("Start failed.");
        }
    }

    private sealed class RecordingRuntimeLease : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopWorkspaceManager : IProfileWorkspaceManager
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
