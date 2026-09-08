using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    private bool _showTrayIcon = true;
    private bool _startWithWindows;
    private bool _startMinimizedToTrayOnWindowsStartup = true;
    private bool _minimizeToTrayOnClose;
    private bool _autoCheckForUpdates = true;
    private bool _showUpdateNotifications = true;
    private LauncherLogLevel _logLevel = LauncherLogLevel.Standard;

    public bool ShowTrayIcon => _showTrayIcon;
    public bool StartWithWindows => _startWithWindows;
    public bool StartMinimizedToTrayOnWindowsStartup => _startMinimizedToTrayOnWindowsStartup;
    public bool MinimizeToTrayOnClose => _minimizeToTrayOnClose;
    public bool AutoCheckForUpdates => _autoCheckForUpdates;
    public bool ShowUpdateNotifications => _showUpdateNotifications;
    public LauncherLogLevel LogLevel => _logLevel;

    public LauncherSettingsViewModel CreateLauncherSettingsViewModel(
        Func<Task<LauncherUpdateResult>>? checkForUpdates = null,
        Func<bool>? confirmReset = null) => new(
        GetLauncherPreferences(),
        _paths.ConfigDirectory,
        ApplyLauncherSettingsAsync,
        _dialogService,
        checkForUpdates,
        confirmReset,
        isPortable: _paths.IsPortable);

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
            _showTrayIcon = preferences.ShowTrayIcon;
            _startWithWindows = preferences.StartWithWindows;
            _startMinimizedToTrayOnWindowsStartup = preferences.StartMinimizedToTrayOnWindowsStartup;
            _isPdaInterfaceEnabled = preferences.IsPdaInterfaceEnabled;
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
            _showTrayIcon = previous.ShowTrayIcon;
            _startWithWindows = previous.StartWithWindows;
            _startMinimizedToTrayOnWindowsStartup = previous.StartMinimizedToTrayOnWindowsStartup;
            _isPdaInterfaceEnabled = previous.IsPdaInterfaceEnabled;
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
        OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        OnPropertyChanged(nameof(AutoCheckForUpdates));
        OnPropertyChanged(nameof(ShowUpdateNotifications));
        OnPropertyChanged(nameof(LogLevel));
    }

    private LauncherPreferences GetLauncherPreferences() => new(
        IsPdaInterfaceEnabled,
        ShowTrayIcon,
        StartWithWindows,
        StartMinimizedToTrayOnWindowsStartup,
        MinimizeToTrayOnClose,
        AutoCheckForUpdates,
        ShowUpdateNotifications,
        LogLevel);
}
