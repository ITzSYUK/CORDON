using System.Net.Http;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class LauncherSettingsViewModel : ObservableObject
{
    private readonly string _settingsDirectory;
    private readonly Func<LauncherPreferences, Task> _save;
    private readonly DialogService _dialogService;
    private readonly Func<Task<LauncherUpdateResult>>? _checkForUpdates;
    private readonly Func<string, string, LauncherReleasePackage, Task<string>> _downloadReleasePackage;
    private readonly Func<string, bool> _confirmInstall;
    private readonly Func<bool> _canInstallUpdate;
    private readonly Action? _requestExit;
    private readonly bool _isStandaloneBuild;
    private readonly Func<bool> _confirmReset;
    private readonly string _initialUiLanguage;
    private string _uiLanguage;
    private bool _showTrayIcon;
    private bool _startWithWindows;
    private bool _startMinimizedToTrayOnWindowsStartup;
    private bool _isPdaInterfaceEnabled;
    private bool _useNewPdaInterface;
    private bool _minimizeToTrayOnClose;
    private bool _autoCheckForUpdates;
    private bool _showUpdateNotifications;
    private LauncherLogLevel _logLevel;
    private bool _isSaving;
    private string _updateStatus = string.Empty;
    private string? _releaseUrl;
    private string? _releaseTag;
    private bool _areDownloadOptionsVisible;
    private string? _downloadedReleaseDirectory;

    public LauncherSettingsViewModel(
        LauncherPreferences preferences,
        string settingsDirectory,
        Func<LauncherPreferences, Task> save,
        DialogService dialogService,
        Func<Task<LauncherUpdateResult>>? checkForUpdates = null,
        Func<bool>? confirmReset = null,
        Func<string, string, LauncherReleasePackage, Task<string>>? downloadReleasePackage = null,
        bool isPortable = false,
        bool? isStandaloneBuild = null,
        Func<string, bool>? confirmInstall = null,
        Func<bool>? canInstallUpdate = null,
        Action? requestExit = null)
    {
        _initialUiLanguage = Services.UiLanguage.Normalize(preferences.UiLanguage);
        _uiLanguage = _initialUiLanguage;
        StorageDescription = isPortable
            ? Strings.Settings_StoragePortable
            : Strings.Settings_StorageRegular;
        _isPdaInterfaceEnabled = preferences.IsPdaInterfaceEnabled;
        _useNewPdaInterface = preferences.UseNewPdaInterface;
        _showTrayIcon = preferences.ShowTrayIcon;
        _startWithWindows = preferences.StartWithWindows;
        _startMinimizedToTrayOnWindowsStartup =
            preferences.ShowTrayIcon && preferences.StartMinimizedToTrayOnWindowsStartup;
        _minimizeToTrayOnClose = preferences.ShowTrayIcon && preferences.MinimizeToTrayOnClose;
        _autoCheckForUpdates = preferences.AutoCheckForUpdates;
        _showUpdateNotifications = preferences.ShowUpdateNotifications;
        _logLevel = preferences.LogLevel;
        _settingsDirectory = settingsDirectory;
        _save = save;
        _dialogService = dialogService;
        _checkForUpdates = checkForUpdates;
        _downloadReleasePackage = downloadReleasePackage ??
            ((releaseUrl, releaseTag, package) => LauncherReleaseDownloadService.DownloadAsync(
                releaseUrl,
                releaseTag,
                package));
        _isStandaloneBuild = isStandaloneBuild ?? AppPaths.IsStandaloneExecutable(Environment.ProcessPath);
        _confirmInstall = confirmInstall ?? (message => DialogService.Confirm(Strings.Update_InstallTitle, message));
        _canInstallUpdate = canInstallUpdate ?? (() => true);
        _requestExit = requestExit;
        _confirmReset = confirmReset ?? (() => DialogService.Confirm(
            Strings.Settings_ResetTitle,
            Strings.Settings_ResetMessage));
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => _checkForUpdates is not null);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => HasAvailableUpdate);
        ShowDownloadOptionsCommand = new RelayCommand(ShowDownloadOptions, () => CanShowDownloadButton);
        DownloadMinimalCommand = new AsyncRelayCommand(
            () => DownloadReleasePackageAsync(LauncherReleasePackage.Minimal),
            () => HasAvailableUpdate);
        DownloadStandaloneCommand = new AsyncRelayCommand(
            () => DownloadReleasePackageAsync(LauncherReleasePackage.Standalone),
            () => HasAvailableUpdate);
        OpenDownloadsCommand = new RelayCommand(OpenDownloadsFolder, () => HasDownloadedRelease);
        ResetCommand = new RelayCommand(ResetToDefaults);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                OnPropertyChanged(nameof(CanStartMinimizedToTray));
            }
        }
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set
        {
            if (!SetProperty(ref _showTrayIcon, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanUseTray));
            OnPropertyChanged(nameof(CanStartMinimizedToTray));
            if (!value)
            {
                StartMinimizedToTrayOnWindowsStartup = false;
                MinimizeToTrayOnClose = false;
            }
        }
    }

    public bool CanUseTray => ShowTrayIcon;

    public bool CanStartMinimizedToTray => ShowTrayIcon && StartWithWindows;

    public bool StartMinimizedToTrayOnWindowsStartup
    {
        get => _startMinimizedToTrayOnWindowsStartup;
        set => SetProperty(ref _startMinimizedToTrayOnWindowsStartup, value);
    }

    public bool UseClassicInterface
    {
        get => !_isPdaInterfaceEnabled;
        set
        {
            if (value)
            {
                SetPdaInterface(false);
            }
        }
    }

    public bool UsePdaInterface
    {
        get => _isPdaInterfaceEnabled && !_useNewPdaInterface;
        set
        {
            if (value)
            {
                SetPdaInterface(true, false);
            }
        }
    }

    public bool UseNewPdaInterface
    {
        get => _isPdaInterfaceEnabled && _useNewPdaInterface;
        set
        {
            if (value)
            {
                SetPdaInterface(true, true);
            }
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public bool AutoCheckForUpdates
    {
        get => _autoCheckForUpdates;
        set => SetProperty(ref _autoCheckForUpdates, value);
    }

    public bool ShowUpdateNotifications
    {
        get => _showUpdateNotifications;
        set => SetProperty(ref _showUpdateNotifications, value);
    }

    public LauncherLogLevel LogLevel
    {
        get => _logLevel;
        set => SetProperty(ref _logLevel, value);
    }

    public string UiLanguage
    {
        get => _uiLanguage;
        set
        {
            if (SetProperty(ref _uiLanguage, Services.UiLanguage.Normalize(value)))
            {
                OnPropertyChanged(nameof(IsLanguageChangePending));
            }
        }
    }

    public bool IsLanguageChangePending => UiLanguage != _initialUiLanguage;

    public bool IsSaving
    {
        get => _isSaving;
        private set => SetProperty(ref _isSaving, value);
    }

    public string SettingsDirectory => _settingsDirectory;
    public string StorageDescription { get; }
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public bool HasAvailableUpdate => !string.IsNullOrWhiteSpace(_releaseUrl);
    public bool AreDownloadOptionsVisible
    {
        get => _areDownloadOptionsVisible;
        private set
        {
            if (SetProperty(ref _areDownloadOptionsVisible, value))
            {
                OnPropertyChanged(nameof(CanShowDownloadButton));
                ShowDownloadOptionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanShowDownloadButton => HasAvailableUpdate && !AreDownloadOptionsVisible;
    public bool HasDownloadedRelease => !string.IsNullOrWhiteSpace(_downloadedReleaseDirectory);
    public RelayCommand OpenSettingsFolderCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand ShowDownloadOptionsCommand { get; }
    public AsyncRelayCommand DownloadMinimalCommand { get; }
    public AsyncRelayCommand DownloadStandaloneCommand { get; }
    public RelayCommand OpenDownloadsCommand { get; }
    public RelayCommand ResetCommand { get; }

    public async Task CheckForUpdatesAsync()
    {
        if (_checkForUpdates is null)
        {
            return;
        }

        SetRelease(null);
        UpdateStatus = Strings.Update_Checking;

        try
        {
            var result = await _checkForUpdates();
            if (result.IsUpdateAvailable)
            {
                SetRelease(result.ReleaseUrl, result.LatestVersion);
                UpdateStatus = LocalizedText.Format(Strings.Update_AvailableFormat, result.LatestVersion, result.CurrentVersion);
            }
            else
            {
                UpdateStatus = LocalizedText.Format(Strings.Update_CurrentFormat, result.CurrentVersion);
            }
        }
        catch (TaskCanceledException)
        {
            UpdateStatus = Strings.Update_Timeout;
        }
        catch (HttpRequestException)
        {
            UpdateStatus = Strings.Update_ConnectionFailed;
        }
        catch (Exception ex)
        {
            UpdateStatus = LocalizedText.Format(Strings.Update_CheckFailedFormat, ex.Message);
        }
    }

    public async Task<bool> TrySaveAsync()
    {
        if (IsSaving)
        {
            return false;
        }

        IsSaving = true;
        try
        {
            await _save(new LauncherPreferences(
                UiLanguage,
                _isPdaInterfaceEnabled,
                _useNewPdaInterface,
                ShowTrayIcon,
                StartWithWindows,
                StartMinimizedToTrayOnWindowsStartup,
                MinimizeToTrayOnClose,
                AutoCheckForUpdates,
                ShowUpdateNotifications,
                LogLevel));
            return true;
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(Strings.Settings_SaveFailed, ex.Message);
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            DialogService.OpenFolder(_settingsDirectory);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(Strings.Settings_OpenFolderFailed, ex.Message);
        }
    }

    private void ResetToDefaults()
    {
        if (!_confirmReset())
        {
            return;
        }

        var defaults = LauncherPreferences.Default;
        UiLanguage = defaults.UiLanguage;
        SetPdaInterface(defaults.IsPdaInterfaceEnabled, defaults.UseNewPdaInterface);
        ShowTrayIcon = defaults.ShowTrayIcon;
        StartWithWindows = defaults.StartWithWindows;
        StartMinimizedToTrayOnWindowsStartup = defaults.StartMinimizedToTrayOnWindowsStartup;
        MinimizeToTrayOnClose = defaults.MinimizeToTrayOnClose;
        AutoCheckForUpdates = defaults.AutoCheckForUpdates;
        ShowUpdateNotifications = defaults.ShowUpdateNotifications;
        LogLevel = defaults.LogLevel;
    }

    private void OpenRelease()
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl))
        {
            return;
        }

        try
        {
            DialogService.OpenUrl(_releaseUrl);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(Strings.Update_OpenReleaseFailed, ex.Message);
        }
    }

    private void ShowDownloadOptions() => AreDownloadOptionsVisible = true;

    private async Task DownloadReleasePackageAsync(LauncherReleasePackage package)
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl) || string.IsNullOrWhiteSpace(_releaseTag))
        {
            return;
        }

        if (!_canInstallUpdate())
        {
            UpdateStatus = Strings.Update_Busy;
            return;
        }

        if (!_confirmInstall(BuildInstallConfirmationMessage(package)))
        {
            return;
        }

        var packageName = package == LauncherReleasePackage.Minimal
            ? Strings.Update_MinimalPackage
            : Strings.Update_StandalonePackage;
        UpdateStatus = LocalizedText.Format(Strings.Update_DownloadingFormat, packageName);

        try
        {
            var path = await _downloadReleasePackage(_releaseUrl, _releaseTag, package);
            SetDownloadedReleaseDirectory(Path.GetDirectoryName(path));
            UpdateStatus = LocalizedText.Format(Strings.Update_ReadyFormat, packageName, Path.GetFileName(path));
            _requestExit?.Invoke();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            UpdateStatus = LocalizedText.Format(Strings.Update_HttpErrorFormat, (int)ex.StatusCode.Value);
        }
        catch (HttpRequestException)
        {
            UpdateStatus = LocalizedText.Format(Strings.Update_DownloadConnectionFormat, packageName);
        }
        catch (Exception ex)
        {
            UpdateStatus = LocalizedText.Format(Strings.Update_DownloadFailedFormat, packageName, ex.Message);
        }
    }

    private string BuildInstallConfirmationMessage(LauncherReleasePackage package)
    {
        var selectedStandalone = package == LauncherReleasePackage.Standalone;
        var mismatchWarning = selectedStandalone == _isStandaloneBuild
            ? string.Empty
            : _isStandaloneBuild
                ? Strings.Update_MismatchFromStandalone
                : Strings.Update_MismatchToStandalone;

        return LocalizedText.Format(Strings.Update_InstallMessageFormat, mismatchWarning);
    }

    private void SetRelease(string? releaseUrl, string? releaseTag = null)
    {
        _releaseUrl = releaseUrl;
        _releaseTag = releaseTag;
        SetDownloadedReleaseDirectory(null);
        AreDownloadOptionsVisible = false;
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(CanShowDownloadButton));
        OpenReleaseCommand.RaiseCanExecuteChanged();
        ShowDownloadOptionsCommand.RaiseCanExecuteChanged();
        DownloadMinimalCommand.RaiseCanExecuteChanged();
        DownloadStandaloneCommand.RaiseCanExecuteChanged();
    }

    private void SetDownloadedReleaseDirectory(string? directory)
    {
        _downloadedReleaseDirectory = directory;
        OnPropertyChanged(nameof(HasDownloadedRelease));
        OpenDownloadsCommand.RaiseCanExecuteChanged();
    }

    private void OpenDownloadsFolder()
    {
        if (string.IsNullOrWhiteSpace(_downloadedReleaseDirectory))
        {
            return;
        }

        try
        {
            DialogService.OpenFolder(_downloadedReleaseDirectory);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(Strings.Update_OpenDownloadsFailed, ex.Message);
        }
    }

    private void SetPdaInterface(bool value, bool useNewPdaInterface = false)
    {
        if (_isPdaInterfaceEnabled == value && _useNewPdaInterface == useNewPdaInterface)
        {
            return;
        }

        _isPdaInterfaceEnabled = value;
        _useNewPdaInterface = useNewPdaInterface;
        OnPropertyChanged(nameof(UseClassicInterface));
        OnPropertyChanged(nameof(UsePdaInterface));
        OnPropertyChanged(nameof(UseNewPdaInterface));
    }
}
