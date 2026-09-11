namespace StalkerModLauncher.Models;

public sealed record LauncherPreferences(
    bool IsPdaInterfaceEnabled,
    bool UseNewPdaInterface,
    bool ShowTrayIcon,
    bool StartWithWindows,
    bool StartMinimizedToTrayOnWindowsStartup,
    bool MinimizeToTrayOnClose,
    bool AutoCheckForUpdates,
    bool ShowUpdateNotifications,
    LauncherLogLevel LogLevel)
{
    public static LauncherPreferences Default { get; } = new(
        IsPdaInterfaceEnabled: false,
        UseNewPdaInterface: false,
        ShowTrayIcon: true,
        StartWithWindows: false,
        StartMinimizedToTrayOnWindowsStartup: true,
        MinimizeToTrayOnClose: false,
        AutoCheckForUpdates: true,
        ShowUpdateNotifications: true,
        LauncherLogLevel.Standard);
}
