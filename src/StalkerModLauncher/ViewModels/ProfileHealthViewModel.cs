using System.Collections.ObjectModel;
using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ProfileHealthViewModel : ObservableObject, IDisposable
{
    private readonly ModProfile _profile;
    private readonly ProfileHealthService _healthService;
    private readonly DialogService _dialogService;
    private readonly WorkspaceManagementService _workspaceManagementService;
    private readonly Action<string, LauncherLogLevel>? _log;
    private ProfileHealthReport? _report;
    private string _summary = Strings.Health_Checking;
    private bool _isChecking;
    private WorkspaceStatus? _workspace;
    private CancellationTokenSource? _refreshCancellation;

    public ProfileHealthViewModel(
        ModProfile profile,
        ProfileHealthService healthService,
        DialogService dialogService,
        WorkspaceManagementService workspaceManagementService,
        Action<string, LauncherLogLevel>? log = null)
    {
        _profile = profile;
        _healthService = healthService;
        _dialogService = dialogService;
        _workspaceManagementService = workspaceManagementService;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsChecking);
        OpenProfileCommand = new RelayCommand(OpenProfile, () => Directory.Exists(_report?.ProfileFolderPath));
        OpenSavesCommand = new RelayCommand(OpenSaves, () => Directory.Exists(_report?.SavedGamesPath));
        OpenLatestLogCommand = new RelayCommand(OpenLatestLog, () => File.Exists(_report?.LatestLogPath));
        OpenCrashDumpCommand = new RelayCommand(OpenCrashDump, () => File.Exists(_report?.LatestCrashDumpPath));
        CopyReportCommand = new RelayCommand(CopyReport, () => _report is not null);
        ClearWorkspaceCommand = new RelayCommand(ClearWorkspace, () => CanManageWorkspace && UsesLinkedWorkspace);
        RebuildWorkspaceCommand = new AsyncRelayCommand(RebuildWorkspaceAsync, () => CanManageWorkspace && UsesLinkedWorkspace && !IsChecking);
        MoveWorkspaceCommand = new AsyncRelayCommand(MoveWorkspaceAsync, () => CanManageWorkspace && !IsChecking);

        _ = RefreshAsync();
    }

    public string ProfileName => _profile.Name;
    public ObservableCollection<ProfileHealthCheck> Checks { get; } = new();

    public string ProfileKind => _profile.IsStandalone
        ? Strings.Profile_Standalone
        : Strings.Health_RegularProfile;

    public WorkspaceStatus? Workspace
    {
        get => _workspace;
        private set
        {
            if (SetProperty(ref _workspace, value))
            {
                OnPropertyChanged(nameof(CanManageWorkspace));
                RaiseStorageProperties();
            }
        }
    }

    public bool ShowStoragePanel => !_profile.IsStandalone;
    public bool CanManageWorkspace => ShowStoragePanel && !string.IsNullOrWhiteSpace(_profile.WorkspacePath);
    public bool UsesLinkedWorkspace => !_profile.IsStandalone && _profile.LaunchBackendKind == LaunchBackendKind.LinkedWorkspace;
    public bool UsesVirtualFileSystem => !_profile.IsStandalone && _profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem;
    public string StoragePanelTitle => UsesVirtualFileSystem ? "USVFS" : "Workspace";
    public string? StoragePanelToolTip => UsesVirtualFileSystem
        ? Strings.Health_UsvfsTooltip
        : null;
    public string StorageStateDisplay => Workspace?.StateDisplay ?? Strings.Health_StatePending;
    public string FirstMetricTitle => UsesVirtualFileSystem ? Strings.Health_Layers : Strings.Health_VisibleSize;
    public string FirstMetricValue => UsesVirtualFileSystem
        ? $"{1 + _profile.Mods.Count(mod => mod.IsEnabled):N0}"
        : Workspace?.LogicalSizeDisplay ?? "—";
    public string FirstMetricToolTip => UsesVirtualFileSystem
        ? Strings.Health_LayersTooltip
        : Strings.Health_VisibleSizeTooltip;
    public string SecondMetricTitle => UsesVirtualFileSystem ? Strings.Health_GameData : Strings.Health_Occupied;
    public string SecondMetricValue => UsesVirtualFileSystem
        ? ProfileDataStateDisplay
        : Workspace?.PhysicalSizeDisplay ?? "—";
    public string SecondMetricToolTip => UsesVirtualFileSystem
        ? _profile.UseBaseGameData ? Strings.Health_SharedData : Strings.Health_SeparateData
        : Strings.Health_OccupiedTooltip;
    public string ThirdMetricTitle => UsesVirtualFileSystem ? Strings.Health_CurrentFolder : Strings.Health_Files;
    public string ThirdMetricValue => UsesVirtualFileSystem ? Strings.Health_NotUsed : Workspace?.FileCountDisplay ?? "—";
    public string ThirdMetricToolTip => UsesVirtualFileSystem
        ? Strings.Health_CurrentTooltip
        : Workspace?.LinkSummaryDisplay ?? string.Empty;

    private string ProfileDataStateDisplay => ProfileDataPathResolver.GetSavedGameDirectories(_profile)
                                              .Any(path => Directory.Exists(Path.GetDirectoryName(path)))
        ? Strings.Health_DataCreated
        : Strings.Health_DataOnLaunch;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)RebuildWorkspaceCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)MoveWorkspaceCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenProfileCommand { get; }
    public ICommand OpenSavesCommand { get; }
    public ICommand OpenLatestLogCommand { get; }
    public ICommand OpenCrashDumpCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand ClearWorkspaceCommand { get; }
    public ICommand RebuildWorkspaceCommand { get; }
    public ICommand MoveWorkspaceCommand { get; }

    private async Task RefreshAsync()
    {
        if (_refreshCancellation is not null)
        {
            await _refreshCancellation.CancelAsync();
        }

        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();

        try
        {
            IsChecking = true;
            Summary = Strings.Health_Checking;
            var report = await _healthService.AnalyzeAsync(_profile, _refreshCancellation.Token);
            _report = report;
            Workspace = report.Workspace;
            RaiseStorageProperties();
            Checks.Clear();
            foreach (var check in report.Checks)
            {
                Checks.Add(check);
            }

            Summary = report.Summary;
            RaiseCommandStates();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Summary = LocalizedText.Format(Strings.Health_CheckFailedFormat, ex.Message);
            Log(Summary, LauncherLogLevel.ErrorsOnly);
        }
        finally
        {
            IsChecking = false;
        }
    }

    public void Dispose()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

    private void OpenProfile()
    {
        RunAction(() => DialogService.OpenFolder(_report!.ProfileFolderPath));
    }

    private void OpenSaves()
    {
        RunAction(() => DialogService.OpenFolder(_report!.SavedGamesPath));
    }

    private void OpenLatestLog()
    {
        RunAction(() => DialogService.OpenFileLocation(_report!.LatestLogPath!));
    }

    private void OpenCrashDump()
    {
        RunAction(() => DialogService.OpenFileLocation(_report!.LatestCrashDumpPath!));
    }

    private void CopyReport()
    {
        RunAction(() => DialogService.CopyText(_report!.ToText(_profile.Name)));
    }

    private void ClearWorkspace()
    {
        if (!DialogService.Confirm(
                Strings.Health_ClearTitle,
                Strings.Health_ClearConfirm))
        {
            return;
        }

        try
        {
            _workspaceManagementService.ClearCache(_profile, new Progress<string>(ReportWorkspaceProgress));
            Log(LocalizedText.Format(Strings.Log_HealthCacheClearedFormat, _profile.Name));
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_HealthCacheClearFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Health_ClearFailed, ex.Message);
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RebuildWorkspaceAsync()
    {
        try
        {
            IsChecking = true;
            Summary = Strings.Health_Rebuilding;
            Log(LocalizedText.Format(Strings.Log_HealthRebuildStartedFormat, _profile.Name));
            var progress = new Progress<string>(ReportWorkspaceProgress);
            await _workspaceManagementService.RebuildAsync(_profile, progress);
            Log(LocalizedText.Format(Strings.Log_HealthRebuildFinishedFormat, _profile.Name));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_HealthRebuildFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Health_RebuildFailed, ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task MoveWorkspaceAsync()
    {
        var destination = DialogService.PickFolder(Strings.Health_PickWorkspace);
        if (destination is null)
        {
            return;
        }

        if (!DialogService.Confirm(
                UsesVirtualFileSystem ? Strings.Health_MoveDataTitle : Strings.Health_MoveWorkspaceTitle,
                UsesVirtualFileSystem
                    ? LocalizedText.Format(Strings.Health_MoveUsvfsConfirmFormat, Environment.NewLine)
                    : LocalizedText.Format(Strings.Health_MoveWorkspaceConfirmFormat, Environment.NewLine)))
        {
            return;
        }

        try
        {
            IsChecking = true;
            Log(LocalizedText.Format(Strings.Log_HealthMoveStartedFormat, _profile.Name));
            var progress = new Progress<string>(ReportWorkspaceProgress);
            var result = await _workspaceManagementService.MoveAsync(_profile, destination, progress);
            if (!result.WasMoved)
            {
                Log(LocalizedText.Format(Strings.Log_HealthAlreadyThereFormat, result.DestinationPath));
                await RefreshAsync();
                return;
            }

            Log(LocalizedText.Format(Strings.Log_HealthMoveFinishedFormat, _profile.Name));
            if (result.CleanupFailure is not null && result.PreviousWorkspacePath is not null)
            {
                await HandleOldWorkspaceCleanupFailureAsync(result, progress);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_HealthMoveFailedFormat, Environment.NewLine, ex), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Health_MoveFailed, ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task HandleOldWorkspaceCleanupFailureAsync(
        WorkspaceMoveResult result,
        IProgress<string> progress)
    {
        var oldWorkspace = result.PreviousWorkspacePath!;
        Log(
            LocalizedText.Format(Strings.Log_HealthOldFolderRemainsFormat, result.DestinationPath, oldWorkspace, Environment.NewLine, result.CleanupFailure),
            LauncherLogLevel.ErrorsOnly);

        if (!DialogService.Confirm(
                Strings.Health_MovePartialTitle,
                LocalizedText.Format(
                    Strings.Health_MovePartialFormat,
                    Environment.NewLine,
                    result.DestinationPath,
                    oldWorkspace)))
        {
            return;
        }

        progress.Report(LocalizedText.Format(Strings.Log_HealthOldFolderRetryFormat, oldWorkspace));
        var retryFailure = await _workspaceManagementService.RetryOldWorkspaceCleanupAsync(
            _profile,
            oldWorkspace);
        if (retryFailure is null)
        {
            Log(LocalizedText.Format(Strings.Log_HealthOldFolderDeletedFormat, oldWorkspace));
            DialogService.ShowInfo(
                Strings.Health_MoveCompleteTitle,
                LocalizedText.Format(Strings.Health_OldDeletedFormat, Environment.NewLine, oldWorkspace));
            return;
        }

        Log(
            LocalizedText.Format(Strings.Log_HealthOldFolderRetryFailedFormat, oldWorkspace, Environment.NewLine, retryFailure),
            LauncherLogLevel.ErrorsOnly);
        _dialogService.ShowError(
            Strings.Health_OldRemainsTitle,
            LocalizedText.Format(
                Strings.Health_OldRemainsFormat,
                Environment.NewLine,
                result.DestinationPath,
                oldWorkspace,
                retryFailure.Message));
    }

    private void RunAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_HealthActionFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Health_Title, ex.Message);
        }
    }

    private void ReportWorkspaceProgress(string message)
    {
        Summary = message;
        Log(message, LauncherLogLevel.Detailed);
    }

    private void Log(string message, LauncherLogLevel level = LauncherLogLevel.Standard) => _log?.Invoke(message, level);

    private void RaiseCommandStates()
    {
        ((RelayCommand)OpenProfileCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenSavesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenLatestLogCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenCrashDumpCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CopyReportCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearWorkspaceCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RebuildWorkspaceCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)MoveWorkspaceCommand).RaiseCanExecuteChanged();
    }

    private void RaiseStorageProperties()
    {
        OnPropertyChanged(nameof(StorageStateDisplay));
        OnPropertyChanged(nameof(FirstMetricValue));
        OnPropertyChanged(nameof(SecondMetricValue));
        OnPropertyChanged(nameof(ThirdMetricValue));
    }
}
