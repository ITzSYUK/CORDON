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
    private readonly Func<string, string, Task<string>> _downloadReleasePackage;
    private readonly Func<string, bool> _confirmInstall;
    private readonly Func<bool> _canInstallUpdate;
    private readonly Action? _requestExit;
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
    private string? _downloadedReleaseDirectory;

    public LauncherSettingsViewModel(
        LauncherPreferences preferences,
        string settingsDirectory,
        Func<LauncherPreferences, Task> save,
        DialogService dialogService,
        Func<Task<LauncherUpdateResult>>? checkForUpdates = null,
        Func<bool>? confirmReset = null,
        Func<string, string, Task<string>>? downloadReleasePackage = null,
        Func<string, bool>? confirmInstall = null,
        Func<bool>? canInstallUpdate = null,
        Action? requestExit = null,
        Action? openMigrationWizard = null)
    {
        _initialUiLanguage = Services.UiLanguage.Normalize(preferences.UiLanguage);
        _uiLanguage = _initialUiLanguage;
        StorageDescription = Strings.Settings_StoragePortable;
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
            ((releaseUrl, releaseTag) => LauncherReleaseDownloadService.DownloadAsync(releaseUrl, releaseTag));
        _confirmInstall = confirmInstall ?? (message => DialogService.Confirm(Strings.Update_InstallTitle, message));
        _canInstallUpdate = canInstallUpdate ?? (() => true);
        _requestExit = requestExit;
        _confirmReset = confirmReset ?? (() => DialogService.Confirm(
            Strings.Settings_ResetTitle,
            Strings.Settings_ResetMessage));
        OpenMigrationWizardCommand = new RelayCommand(() => openMigrationWizard?.Invoke(), () => openMigrationWizard is not null);
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => _checkForUpdates is not null);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => HasAvailableUpdate);
        DownloadAndInstallCommand = new AsyncRelayCommand(
            DownloadReleasePackageAsync,
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
    public bool HasDownloadedRelease => !string.IsNullOrWhiteSpace(_downloadedReleaseDirectory);
    public RelayCommand OpenMigrationWizardCommand { get; }
    public RelayCommand OpenSettingsFolderCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public AsyncRelayCommand DownloadAndInstallCommand { get; }
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

    private async Task DownloadReleasePackageAsync()
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

        if (!_confirmInstall(LocalizedText.Format(Strings.Update_InstallMessageFormat, string.Empty)))
        {
            return;
        }

        var packageName = Strings.Update_Package;
        UpdateStatus = LocalizedText.Format(Strings.Update_DownloadingFormat, packageName);

        try
        {
            var path = await _downloadReleasePackage(_releaseUrl, _releaseTag);
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

    private void SetRelease(string? releaseUrl, string? releaseTag = null)
    {
        _releaseUrl = releaseUrl;
        _releaseTag = releaseTag;
        SetDownloadedReleaseDirectory(null);
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OpenReleaseCommand.RaiseCanExecuteChanged();
        DownloadAndInstallCommand.RaiseCanExecuteChanged();
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
