using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public sealed class ModProfile : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = Strings.Creation_DefaultName;
    private string _description = string.Empty;
    private bool _isEnabled = true;
    private bool _isDiscordStatusEnabled = true;
    private bool _isStandalone;
    private bool _useBaseGameData;
    private LaunchBackendKind _launchBackendKind = LaunchBackendKind.LinkedWorkspace;
    private string _launchArguments = "-nointro";
    private string _executableRelativePath = @"bin\xr_3da.exe";
    private string _executableSourcePath = string.Empty;
    private string _fsgameSourcePath = string.Empty;
    private string _usvfsExecutableOverrideRelativePath = string.Empty;
    private double _totalPlaytimeSeconds;
    private DateTime? _lastPlayedAt;
    private string _workspacePath = string.Empty;
    private string _workingDirectoryRelative = string.Empty;
    private string _gameInstallPath = string.Empty;
    private string _mo2OverwritePath = string.Empty;
    private string _modInstallPath = string.Empty;
    private bool _isRunning;
    private bool _hasLaunchError;
    private string _launchErrorSummary = string.Empty;
    private List<string> _collapsedModGroups = [];
    private ObservableCollection<ModEntry> _mods = new();

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// Publishes this profile's launch as Discord Rich Presence.
    /// The value is profile-specific because some modpacks already provide their own status.
    /// </summary>
    public bool IsDiscordStatusEnabled
    {
        get => _isDiscordStatusEnabled;
        set => SetProperty(ref _isDiscordStatusEnabled, value);
    }

    public bool IsStandalone
    {
        get => _isStandalone;
        set => SetProperty(ref _isStandalone, value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LaunchBackendKind LaunchBackendKind
    {
        get => _launchBackendKind;
        set => SetProperty(ref _launchBackendKind, value);
    }

    public string LaunchArguments
    {
        get => _launchArguments;
        set => SetProperty(ref _launchArguments, value);
    }

    public string ExecutableRelativePath
    {
        get => _executableRelativePath;
        set => SetProperty(ref _executableRelativePath, value);
    }

    public string ExecutableSourcePath
    {
        get => _executableSourcePath;
        set => SetProperty(ref _executableSourcePath, value);
    }

    public string FsgameSourcePath
    {
        get => _fsgameSourcePath;
        set => SetProperty(ref _fsgameSourcePath, value);
    }

    public string UsvfsExecutableOverrideRelativePath
    {
        get => _usvfsExecutableOverrideRelativePath;
        set => SetProperty(ref _usvfsExecutableOverrideRelativePath, value);
    }

    public double TotalPlaytimeSeconds
    {
        get => _totalPlaytimeSeconds;
        set
        {
            if (SetProperty(ref _totalPlaytimeSeconds, value))
            {
                OnPropertyChanged(nameof(PlaytimeDisplay));
            }
        }
    }

    public DateTime? LastPlayedAt
    {
        get => _lastPlayedAt;
        set
        {
            if (SetProperty(ref _lastPlayedAt, value))
            {
                OnPropertyChanged(nameof(LastPlayedDisplay));
            }
        }
    }

    [JsonIgnore]
    public string PlaytimeDisplay
    {
        get
        {
            var total = TimeSpan.FromSeconds(_totalPlaytimeSeconds);
            if (total.TotalHours >= 1)
            {
                return LocalizedText.Format(Strings.Common_DurationHoursFormat, (int)total.TotalHours, total.Minutes);
            }

            if (total.TotalMinutes >= 1)
            {
                return LocalizedText.Format(Strings.Common_DurationMinutesFormat, (int)total.TotalMinutes);
            }

            return LocalizedText.Format(Strings.Common_DurationSecondsFormat, total.TotalSeconds);
        }
    }

    [JsonIgnore]
    public string LastPlayedDisplay => _lastPlayedAt?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string WorkspacePath
    {
        get => _workspacePath;
        set => SetProperty(ref _workspacePath, value);
    }

    public string WorkingDirectoryRelative
    {
        get => _workingDirectoryRelative;
        set => SetProperty(ref _workingDirectoryRelative, value);
    }

    public string GameInstallPath
    {
        get => _gameInstallPath;
        set => SetProperty(ref _gameInstallPath, value);
    }

    public string Mo2OverwritePath
    {
        get => _mo2OverwritePath;
        set => SetProperty(ref _mo2OverwritePath, value);
    }

    public string ModInstallPath
    {
        get => _modInstallPath;
        set => SetProperty(ref _modInstallPath, value);
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool UseBaseGameData
    {
        get => _useBaseGameData;
        set => SetProperty(ref _useBaseGameData, value);
    }

    [JsonIgnore]
    public bool HasLaunchError
    {
        get => _hasLaunchError;
        set => SetProperty(ref _hasLaunchError, value);
    }

    [JsonIgnore]
    public string LaunchErrorSummary
    {
        get => _launchErrorSummary;
        set => SetProperty(ref _launchErrorSummary, value);
    }

    public ObservableCollection<ModEntry> Mods
    {
        get => _mods;
        set => SetProperty(ref _mods, value);
    }

    public List<string> CollapsedModGroups
    {
        get => _collapsedModGroups;
        set => SetProperty(ref _collapsedModGroups, value ?? []);
    }
}
