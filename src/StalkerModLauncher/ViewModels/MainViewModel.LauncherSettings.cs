using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    private string _uiLanguage = Services.UiLanguage.System;
    private bool _showTrayIcon = true;
    private bool _startWithWindows;
    private bool _startMinimizedToTrayOnWindowsStartup = true;
    private bool _minimizeToTrayOnClose;
    private bool _autoCheckForUpdates = true;
    private bool _showUpdateNotifications = true;
    private LauncherLogLevel _logLevel = LauncherLogLevel.Standard;
    private Task _dataMigrationTask = Task.CompletedTask;
    private bool _isClosing;

    public bool ShowTrayIcon => _showTrayIcon;
    public bool StartWithWindows => _startWithWindows;
    public bool StartMinimizedToTrayOnWindowsStartup => _startMinimizedToTrayOnWindowsStartup;
    public bool MinimizeToTrayOnClose => _minimizeToTrayOnClose;
    public bool AutoCheckForUpdates => _autoCheckForUpdates;
    public bool ShowUpdateNotifications => _showUpdateNotifications;
    public LauncherLogLevel LogLevel => _logLevel;
    public string UiLanguage => _uiLanguage;
    public event EventHandler? LauncherUpdateInstallationRequested;
    public event EventHandler? StorageMigrationRequested;

    public LauncherSettingsViewModel CreateLauncherSettingsViewModel(
        Func<Task<LauncherUpdateResult>>? checkForUpdates = null,
        Func<bool>? confirmReset = null) => new(
        GetLauncherPreferences(),
        _paths.ConfigDirectory,
        ApplyLauncherSettingsAsync,
        _dialogService,
        checkForUpdates,
        confirmReset,
        downloadReleasePackage: (url, tag) => InstallLauncherUpdateAsync(() => LauncherReleaseDownloadService.DownloadAsync(url, tag)),
        canInstallUpdate: CanInstallLauncherUpdate,
        requestExit: () => LauncherUpdateInstallationRequested?.Invoke(this, EventArgs.Empty),
        openMigrationWizard: () => StorageMigrationRequested?.Invoke(this, EventArgs.Empty));

    private bool CanInstallLauncherUpdate() =>
        !IsBuilding && !_isInstallingModArchive && !_isClosing &&
        Profiles.All(profile => !profile.IsRunning && !profile.IsPreparing);

    internal async Task<string> InstallLauncherUpdateAsync(Func<Task<string>> install)
    {
        if (!CanInstallLauncherUpdate()) throw new InvalidOperationException(Strings.Update_Busy);
        IsBuilding = true;
        RaiseCommandStates();
        try
        {
            return await install();
        }
        finally
        {
            IsBuilding = false;
            RaiseCommandStates();
        }
    }

    internal Task RunDataMigrationAsync(IProgress<(double Fraction, string Path)> progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isClosing || IsBuilding || _isInstallingModArchive || Profiles.Any(profile => profile.IsRunning || profile.IsPreparing))
            throw new InvalidOperationException(Strings.Update_Busy);
        return _dataMigrationTask = RunDataMigrationCoreAsync(progress);
    }

    private async Task RunDataMigrationCoreAsync(IProgress<(double Fraction, string Path)> progress)
    {
        IsBuilding = true;
        RaiseCommandStates();
        try
        {
            await SaveOrThrowAsync();
            await _settingsStore.TransferDataAsync(progress);
            await LoadAsync();
        }
        finally
        {
            _autoSave.Cancel();
            IsBuilding = false;
            RaiseCommandStates();
        }
    }

    private async Task ApplyLauncherSettingsAsync(LauncherPreferences preferences)
    {
        var previous = GetLauncherPreferences();
        var startupRegistrationChanged =
            preferences.StartWithWindows != previous.StartWithWindows ||
            preferences.StartWithWindows &&
            preferences.StartMinimizedToTrayOnWindowsStartup != previous.StartMinimizedToTrayOnWindowsStartup;

        if (startupRegistrationChanged)
        {
            _startupRegistrationService.Configure(
                preferences.StartWithWindows,
                preferences.StartMinimizedToTrayOnWindowsStartup);
        }

        try
        {
            _uiLanguage = preferences.UiLanguage;
            _showTrayIcon = preferences.ShowTrayIcon;
            _startWithWindows = preferences.StartWithWindows;
            _startMinimizedToTrayOnWindowsStartup = preferences.StartMinimizedToTrayOnWindowsStartup;
            _isPdaInterfaceEnabled = preferences.IsPdaInterfaceEnabled;
            _useNewPdaInterface = preferences.UseNewPdaInterface;
            _minimizeToTrayOnClose = preferences.MinimizeToTrayOnClose;
            _autoCheckForUpdates = preferences.AutoCheckForUpdates;
            _showUpdateNotifications = preferences.ShowUpdateNotifications;
            _logLevel = preferences.LogLevel;
            _applicationLogService.Level = preferences.LogLevel;
            await SaveOrThrowAsync();
            NotifyLauncherSettingsChanged();
        }
        catch
        {
            _uiLanguage = previous.UiLanguage;
            _showTrayIcon = previous.ShowTrayIcon;
            _startWithWindows = previous.StartWithWindows;
            _startMinimizedToTrayOnWindowsStartup = previous.StartMinimizedToTrayOnWindowsStartup;
            _isPdaInterfaceEnabled = previous.IsPdaInterfaceEnabled;
            _useNewPdaInterface = previous.UseNewPdaInterface;
            _minimizeToTrayOnClose = previous.MinimizeToTrayOnClose;
            _autoCheckForUpdates = previous.AutoCheckForUpdates;
            _showUpdateNotifications = previous.ShowUpdateNotifications;
            _logLevel = previous.LogLevel;
            _applicationLogService.Level = previous.LogLevel;

            if (startupRegistrationChanged)
            {
                try
                {
                    _startupRegistrationService.Configure(
                        previous.StartWithWindows,
                        previous.StartMinimizedToTrayOnWindowsStartup);
                }
                catch
                {
                    // Preserve the original settings error.
                }
            }

            throw;
        }
    }

    private void NotifyLauncherSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowTrayIcon));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimizedToTrayOnWindowsStartup));
        OnPropertyChanged(nameof(IsPdaInterfaceEnabled));
        OnPropertyChanged(nameof(UseNewPdaInterface));
        OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        OnPropertyChanged(nameof(AutoCheckForUpdates));
        OnPropertyChanged(nameof(ShowUpdateNotifications));
        OnPropertyChanged(nameof(LogLevel));
        OnPropertyChanged(nameof(UiLanguage));
    }

    private LauncherPreferences GetLauncherPreferences() => new(
        UiLanguage,
        IsPdaInterfaceEnabled,
        UseNewPdaInterface,
        ShowTrayIcon,
        StartWithWindows,
        StartMinimizedToTrayOnWindowsStartup,
        MinimizeToTrayOnClose,
        AutoCheckForUpdates,
        ShowUpdateNotifications,
        LogLevel);
}
