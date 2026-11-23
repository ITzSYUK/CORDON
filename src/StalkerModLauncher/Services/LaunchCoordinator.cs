using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public sealed class LaunchCoordinator : IDisposable
{
    private readonly IProfileLauncher _profileLauncher;
    private readonly IGameSessionTracker _sessionTracker;
    private readonly GameLaunchReadinessMonitor _readinessMonitor;

    public LaunchCoordinator(
        IProfileLauncher profileLauncher,
        IGameSessionTracker sessionTracker,
        GameLaunchReadinessMonitor? readinessMonitor = null)
    {
        _profileLauncher = profileLauncher;
        _sessionTracker = sessionTracker;
        _readinessMonitor = readinessMonitor ?? new GameLaunchReadinessMonitor();
    }

    public void ConfigureDiscord(string clientId, Action<string>? diagnostic = null)
    {
        _sessionTracker.ConfigureDiscord(clientId, diagnostic);
    }

    public async Task<LaunchedGameSession> StartAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        string? savedGamePath = null,
        CancellationToken cancellationToken = default)
    {
        var launch = await _profileLauncher.LaunchAsync(gamePath, profile, progress, savedGamePath, cancellationToken);
        var completion = _sessionTracker.TrackAsync(
            launch.Process,
            profile.Name,
            profile.IsDiscordStatusEnabled,
            launch.Completion);
        var readiness = _readinessMonitor.MonitorAsync(launch, profile, cancellationToken);
        return new LaunchedGameSession(launch.ProcessId, readiness, completion, launch.TryTerminate);
    }

    public void Dispose()
    {
        _sessionTracker.Dispose();
    }
}

public sealed record LaunchedGameSession(
    int ProcessId,
    Task<GameLaunchReadinessResult> Readiness,
    Task<GameSessionResult> Completion,
    Func<bool> TryTerminate);
