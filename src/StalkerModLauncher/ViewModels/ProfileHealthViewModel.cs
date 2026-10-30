using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public enum HealthCheckFilter
{
    All,
    Warnings,
    Errors
}

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
    private HealthCheckFilter _filter = HealthCheckFilter.All;
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
        SetHealthFilterCommand = new RelayCommand(
            parameter => SetHealthFilter(parameter as HealthCheckFilter? ?? HealthCheckFilter.All));
        MoveWorkspaceCommand = new AsyncRelayCommand(MoveWorkspaceAsync, () => CanManageWorkspace && !IsChecking);

        _profile.PropertyChanged += ProfileOnPropertyChanged;
        Checks.CollectionChanged += ChecksOnCollectionChanged;
        _ = RefreshAsync();
    }

    private void ProfileOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModProfile.IsRunning) or nameof(ModProfile.IsPreparing))
        {
            OnPropertyChanged(nameof(CanManageWorkspace));
            ((AsyncRelayCommand)MoveWorkspaceCommand).RaiseCanExecuteChanged();
        }
    }

    private void SetHealthFilter(HealthCheckFilter requested) =>
        CurrentFilter = ToggleFilter(CurrentFilter, requested);

    private void ChecksOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RaiseFilterProperties();

    private void RaiseFilterProperties()
    {
        OnPropertyChanged(nameof(VisibleChecks));
        OnPropertyChanged(nameof(IsWarningFilterActive));
        OnPropertyChanged(nameof(IsErrorFilterActive));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningsButtonContent));
        OnPropertyChanged(nameof(ErrorsButtonContent));
    }

    public string ProfileName => _profile.Name;
    public ObservableCollection<ProfileHealthCheck> Checks { get; } = new();

    public HealthCheckFilter CurrentFilter
    {
        get => _filter;
        private set
        {
            if (SetProperty(ref _filter, value))
            {
                OnPropertyChanged(nameof(VisibleChecks));
                OnPropertyChanged(nameof(IsWarningFilterActive));
                OnPropertyChanged(nameof(IsErrorFilterActive));
                OnPropertyChanged(nameof(WarningsButtonContent));
                OnPropertyChanged(nameof(ErrorsButtonContent));
            }
        }
    }

    public IEnumerable<ProfileHealthCheck> VisibleChecks =>
        Checks.Where(check => MatchesFilter(check, CurrentFilter));

    public bool IsWarningFilterActive => CurrentFilter == HealthCheckFilter.Warnings;

    public bool IsErrorFilterActive => CurrentFilter == HealthCheckFilter.Errors;

    public int WarningCount => Checks.Count(check => check.Status == ProfileHealthStatus.Warning);

    public int ErrorCount => Checks.Count(check => check.Status == ProfileHealthStatus.Error);

    public string WarningsButtonContent => $"{Strings.Health_Warnings} ({WarningCount})";

    public string ErrorsButtonContent => $"{Strings.Health_Errors} ({ErrorCount})";

    public static bool MatchesFilter(ProfileHealthCheck check, HealthCheckFilter filter) =>
        filter switch
        {
            HealthCheckFilter.Warnings => check.Status == ProfileHealthStatus.Warning,
            HealthCheckFilter.Errors => check.Status == ProfileHealthStatus.Error,
            _ => true
        };

    public static HealthCheckFilter ToggleFilter(HealthCheckFilter current, HealthCheckFilter requested) =>
        requested == current ? HealthCheckFilter.All : requested;

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
    public bool CanManageWorkspace => ShowStoragePanel && !_profile.IsRunning && !_profile.IsPreparing && !string.IsNullOrWhiteSpace(_profile.WorkspacePath);
    public string FirstMetricValue => $"{1 + _profile.Mods.Count(mod => mod.IsEnabled):N0}";
    public string SecondMetricValue => ProfileDataStateDisplay;
    public string SecondMetricToolTip => _profile.UseBaseGameData ? Strings.Health_SharedData : Strings.Health_SeparateData;

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
    public ICommand SetHealthFilterCommand { get; }
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
            CurrentFilter = HealthCheckFilter.All;
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
        _profile.PropertyChanged -= ProfileOnPropertyChanged;
        Checks.CollectionChanged -= ChecksOnCollectionChanged;
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

    private async Task MoveWorkspaceAsync()
    {
        var destination = DialogService.PickFolder(Strings.Health_PickWorkspace);
        if (destination is null)
        {
            return;
        }

        if (!DialogService.Confirm(
                Strings.Health_MoveDataTitle,
                LocalizedText.Format(Strings.Health_MoveUsvfsConfirmFormat, Environment.NewLine)))
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


        ((AsyncRelayCommand)MoveWorkspaceCommand).RaiseCanExecuteChanged();
    }

    private void RaiseStorageProperties()
    {
        OnPropertyChanged(nameof(FirstMetricValue));
        OnPropertyChanged(nameof(SecondMetricValue));

    }
}
