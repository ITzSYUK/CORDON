using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
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
                Log(LocalizedText.Format(Strings.Launch_BlockedFormat, profile.Name), LauncherLogLevel.ErrorsOnly);
                return;
            }

            IsBuilding = true;
            BuildProgressText = Strings.Launch_Checking;
            RaiseCommandStates();

            var preflight = await _launchPreflightService.AnalyzeAsync(profile);
            foreach (var warning in preflight.Checks.Where(check => check.Status == ProfileHealthStatus.Warning))
            {
                Log(LocalizedText.Format(Strings.Launch_WarningFormat, warning.Title, warning.Details), LauncherLogLevel.Standard);
            }

            if (!preflight.CanLaunch)
            {
                throw new InvalidOperationException(preflight.ToErrorMessage());
            }

            BuildProgressText = Strings.Launch_Preparing;
            var progress = new Progress<string>(message =>
            {
                Log(message, LauncherLogLevel.Detailed);
                BuildProgressText = message;
            });

            var session = await _launchCoordinator.StartAsync(profile.GameInstallPath, profile, progress);
            await SaveAsync();
            Log(LocalizedText.Format(Strings.Launch_ProcessCreatedFormat, session.ProcessId), LauncherLogLevel.Detailed);
            profile.IsRunning = true;
            RaiseCommandStates();
            _ = ObserveLaunchReadinessAsync(session, profile);
            _ = CompleteGameSessionAsync(session.Completion, profile);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Launch_ErrorFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Launch_Failed, ex.Message);
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
                Log(LocalizedText.Format(Strings.Launch_ReadyFormat, readiness.Details), LauncherLogLevel.Detailed);
                return;
            }

            if (readiness.Status == GameLaunchReadinessStatus.ExitedBeforeReady)
            {
                Log(LocalizedText.Format(Strings.Launch_ExitedEarlyFormat, readiness.Details), LauncherLogLevel.ErrorsOnly);
                return;
            }

            Log(LocalizedText.Format(Strings.Launch_PossibleHangFormat, readiness.Details), LauncherLogLevel.ErrorsOnly);
            var terminate = false;
            await InvokeOnUiAsync(() =>
            {
                if (!profile.IsRunning)
                {
                    return;
                }

                terminate = DialogService.Confirm(
                    Strings.Launch_PossibleHangTitle,
                    readiness.Details + Environment.NewLine + Environment.NewLine +
                    Strings.Launch_EndProcesses);
            });

            if (terminate)
            {
                Log(session.TryTerminate()
                    ? Strings.Log_HungProcessesTerminated
                    : Strings.Log_NoLaunchProcesses,
                    LauncherLogLevel.ErrorsOnly);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_LaunchReadinessFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
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
                Log(LocalizedText.Format(
                    Strings.Log_PlaytimeRecordedFormat,
                    result.Duration.ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                    profile.PlaytimeDisplay));
            });
            await SaveAsync();
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_PlaytimeFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
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
            var exitCode = diagnostics.ExitCode.HasValue
                ? LocalizedText.Format(Strings.Log_ExitCodeFormat, diagnostics.ExitCode)
                : string.Empty;
            Log(LocalizedText.Format(
                Strings.Log_GameExitedEarlyFormat,
                result.Duration.ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                exitCode), LauncherLogLevel.ErrorsOnly);
        }
        else if (diagnostics.ExitCode is not null and not 0)
        {
            Log(LocalizedText.Format(Strings.Log_GameExitedCodeFormat, diagnostics.ExitCode), LauncherLogLevel.ErrorsOnly);
        }

        if (diagnostics.IsSuspiciousExit && diagnostics.LatestLogPath is not null)
        {
            Log(LocalizedText.Format(Strings.Log_LatestGameLogFormat, diagnostics.LatestLogPath), LauncherLogLevel.Detailed);
        }

        if (diagnostics.LatestCrashDumpPath is not null)
        {
            Log(LocalizedText.Format(Strings.Log_CrashDumpDetectedFormat, diagnostics.LatestCrashDumpPath), LauncherLogLevel.ErrorsOnly);
        }
    }
}
