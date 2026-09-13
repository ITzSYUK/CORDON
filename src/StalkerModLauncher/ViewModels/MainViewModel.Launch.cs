using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    private bool CanLaunch()
    {
        return !IsBuilding && IsGameValid && SelectedProfile is { IsEnabled: true, IsRunning: false };
    }

    public bool CanLaunchProfile(ModProfile profile) =>
        !IsBuilding &&
        profile is { IsEnabled: true, IsRunning: false } &&
        GetProfileValidation(profile).IsValid;

    private Task LaunchAsync() => SelectedProfile is { } profile
        ? LaunchProfileAsync(profile)
        : Task.CompletedTask;

    public async Task LaunchProfileAsync(ModProfile profile)
    {
        try
        {
            if (IsBuilding ||
                profile is not { IsEnabled: true, IsRunning: false } ||
                !GetProfileValidation(profile, forceRefresh: true).IsValid)
            {
                Log($"Запуск заблокирован: профиль «{profile.Name}» не готов.", LauncherLogLevel.ErrorsOnly);
                return;
            }

            IsBuilding = true;
            BuildProgressText = "Проверка профиля перед запуском...";
            RaiseCommandStates();

            var preflight = await _launchPreflightService.AnalyzeAsync(profile);
            foreach (var warning in preflight.Checks.Where(check => check.Status == ProfileHealthStatus.Warning))
            {
                Log($"Предупреждение проверки: {warning.Title}: {warning.Details}", LauncherLogLevel.Standard);
            }

            if (!preflight.CanLaunch)
            {
                throw new InvalidOperationException(preflight.ToErrorMessage());
            }

            BuildProgressText = "Подготовка запуска...";
            var progress = new Progress<string>(message =>
            {
                Log(message, LauncherLogLevel.Detailed);
                BuildProgressText = message;
            });

            var session = await _launchCoordinator.StartAsync(profile.GameInstallPath, profile, progress);
            await SaveAsync();
            Log($"Процесс игры создан. PID: {session.ProcessId}", LauncherLogLevel.Detailed);
            profile.IsRunning = true;
            RaiseCommandStates();
            _ = ObserveLaunchReadinessAsync(session, profile);
            _ = CompleteGameSessionAsync(session.Completion, profile);
        }
        catch (Exception ex)
        {
            Log($"Ошибка запуска: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError("Не удалось запустить профиль", ex.Message);
        }
        finally
        {
            IsBuilding = false;
            BuildProgressText = string.Empty;
            RaiseCommandStates();
        }
    }

    private async Task ObserveLaunchReadinessAsync(LaunchedGameSession session, ModProfile profile)
    {
        try
        {
            var readiness = await session.Readiness;
            if (readiness.Status == GameLaunchReadinessStatus.Ready)
            {
                Log($"Игра готова к работе: {readiness.Details}.", LauncherLogLevel.Detailed);
                return;
            }

            if (readiness.Status == GameLaunchReadinessStatus.ExitedBeforeReady)
            {
                Log($"Игра завершилась до подтверждения готовности: {readiness.Details}", LauncherLogLevel.ErrorsOnly);
                return;
            }

            Log($"Возможное зависание запуска: {readiness.Details}", LauncherLogLevel.ErrorsOnly);
            var terminate = false;
            await InvokeOnUiAsync(() =>
            {
                if (!profile.IsRunning)
                {
                    return;
                }

                terminate = DialogService.Confirm(
                    "Возможное зависание запуска",
                    readiness.Details + Environment.NewLine + Environment.NewLine +
                    "Завершить связанные процессы? Нажмите «Нет», чтобы продолжить ожидание.");
            });

            if (terminate)
            {
                Log(session.TryTerminate()
                    ? "Hung launch processes were terminated by the user."
                    : "No active launch processes were found to terminate.",
                    LauncherLogLevel.ErrorsOnly);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"Launch readiness check failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
        }
    }

    private async Task CompleteGameSessionAsync(Task<GameSessionResult> sessionTask, ModProfile profile)
    {
        try
        {
            var result = await sessionTask;
            await InvokeOnUiAsync(() =>
            {
                profile.IsRunning = false;
                RaiseCommandStates();
                LogGameExitDiagnostics(profile, result);
            });

            if (!result.ShouldRecord)
            {
                return;
            }

            await InvokeOnUiAsync(() =>
            {
                profile.TotalPlaytimeSeconds += result.Duration.TotalSeconds;
                profile.LastPlayedAt = DateTime.Now;
                Log($"Playtime recorded: {result.Duration:g} (total: {profile.PlaytimeDisplay})");
            });
            await SaveAsync();
        }
        catch (Exception ex)
        {
            Log($"Playtime tracking failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            await InvokeOnUiAsync(() =>
            {
                profile.IsRunning = false;
                RaiseCommandStates();
            });
        }
    }

    private void LogGameExitDiagnostics(ModProfile profile, GameSessionResult result)
    {
        var diagnostics = GameExitDiagnosticsService.Analyze(profile, result);
        if (diagnostics.IsQuickExit)
        {
            var exitCode = diagnostics.ExitCode.HasValue ? $" Exit code: {diagnostics.ExitCode}." : string.Empty;
            Log($"Game exited shortly after launch ({result.Duration:g}).{exitCode}", LauncherLogLevel.ErrorsOnly);
        }
        else if (diagnostics.ExitCode is not null and not 0)
        {
            Log($"Game process exited with code {diagnostics.ExitCode}.", LauncherLogLevel.ErrorsOnly);
        }

        if (diagnostics.IsSuspiciousExit && diagnostics.LatestLogPath is not null)
        {
            Log($"Latest game log: {diagnostics.LatestLogPath}", LauncherLogLevel.Detailed);
        }

        if (diagnostics.LatestCrashDumpPath is not null)
        {
            Log($"Crash dump detected: {diagnostics.LatestCrashDumpPath}", LauncherLogLevel.ErrorsOnly);
        }
    }
}
