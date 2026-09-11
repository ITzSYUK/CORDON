using System.Text.Json.Serialization;

namespace StalkerModLauncher.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 8;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string LastBrowsedGamePath { get; set; } = string.Empty;

    [JsonPropertyName("GameInstallPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGameInstallPath { get; set; }

    public List<ModProfile> Profiles { get; set; } = new();
    public bool DontShowAboutOnStartup { get; set; }
    public bool IsLogVisible { get; set; } = true;
    public bool IsPdaInterfaceEnabled { get; set; }
    public bool UseNewPdaInterface { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTrayOnWindowsStartup { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; }
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool ShowUpdateNotifications { get; set; } = true;
    public LauncherLogLevel LogLevel { get; set; } = LauncherLogLevel.Standard;
    public string DiscordClientId { get; set; } = "1510923765431799898";
}
