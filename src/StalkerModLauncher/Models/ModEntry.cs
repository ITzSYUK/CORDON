using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;
using System.Text.Json.Serialization;

namespace StalkerModLauncher.Models;

public sealed class ModEntry : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = Strings.Mod_DefaultName;
    private string _sourcePath = string.Empty;
    private string _groupName = string.Empty;
    private ModGroupKey _viewGroupKey = ModGroupKey.Ungrouped(string.Empty);
    private bool _showsGroupHeader;
    private bool _isGroupCollapsed;
    private bool _isVisibleInModList = true;
    private bool _isEnabled = true;
    private List<string> _excludedFiles = [];
    private ModConflictKind _conflictKind;
    private bool _hasOverlapsAbove;
    private int _overwrittenFileCount;
    private int _overwrittenModCount;
    private bool _providesLaunchExecutable;
    private int _overwrittenConfigurationCount;
    private int _overwrittenBinaryCount;
    private int _overwrittenByFileCount;
    private int _overwrittenByModCount;
    private int _overwrittenByBinaryCount;
    private string _overlayDetails = string.Empty;
    private IReadOnlyList<string> _relatedModIds = [];
    private bool _isConflictRelated;
    private int _order;

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

    public string SourcePath
    {
        get => _sourcePath;
        set => SetProperty(ref _sourcePath, value);
    }

    public string GroupName
    {
        get => _groupName;
        set => SetProperty(ref _groupName, value ?? string.Empty);
    }

    [JsonIgnore]
    public ModGroupKey ViewGroupKey
    {
        get => _viewGroupKey;
        internal set => SetProperty(ref _viewGroupKey, value);
    }

    [JsonIgnore]
    public bool ShowsGroupHeader
    {
        get => _showsGroupHeader;
        internal set => SetProperty(ref _showsGroupHeader, value);
    }

    [JsonIgnore]
    public bool IsGroupCollapsed
    {
        get => _isGroupCollapsed;
        internal set => SetProperty(ref _isGroupCollapsed, value);
    }

    [JsonIgnore]
    public bool IsVisibleInModList
    {
        get => _isVisibleInModList;
        internal set => SetProperty(ref _isVisibleInModList, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public List<string> ExcludedFiles
    {
        get => _excludedFiles;
        set => SetProperty(ref _excludedFiles, value ?? []);
    }

    [JsonIgnore]
    public ModConflictKind ConflictKind
    {
        get => _conflictKind;
        set
        {
            if (SetProperty(ref _conflictKind, value))
            {
                OnPropertyChanged(nameof(ConflictDisplay));
                OnPropertyChanged(nameof(OverlaySummary));
                OnPropertyChanged(nameof(HasOverlayInfo));
            }
        }
    }

    [JsonIgnore]
    public bool HasOverlapsAbove
    {
        get => _hasOverlapsAbove;
        set => SetProperty(ref _hasOverlapsAbove, value);
    }

    [JsonIgnore]
    public int OverwrittenFileCount
    {
        get => _overwrittenFileCount;
        set
        {
            if (SetProperty(ref _overwrittenFileCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
                OnPropertyChanged(nameof(HasOverlayInfo));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenModCount
    {
        get => _overwrittenModCount;
        set
        {
            if (SetProperty(ref _overwrittenModCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
            }
        }
    }

    [JsonIgnore]
    public bool ProvidesLaunchExecutable
    {
        get => _providesLaunchExecutable;
        set
        {
            if (SetProperty(ref _providesLaunchExecutable, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
                OnPropertyChanged(nameof(HasOverlayInfo));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenConfigurationCount
    {
        get => _overwrittenConfigurationCount;
        set
        {
            if (SetProperty(ref _overwrittenConfigurationCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenBinaryCount
    {
        get => _overwrittenBinaryCount;
        set
        {
            if (SetProperty(ref _overwrittenBinaryCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenByFileCount
    {
        get => _overwrittenByFileCount;
        set
        {
            if (SetProperty(ref _overwrittenByFileCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
                OnPropertyChanged(nameof(HasOverlayInfo));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenByModCount
    {
        get => _overwrittenByModCount;
        set
        {
            if (SetProperty(ref _overwrittenByModCount, value))
            {
                OnPropertyChanged(nameof(OverlaySummary));
            }
        }
    }

    [JsonIgnore]
    public int OverwrittenByBinaryCount
    {
        get => _overwrittenByBinaryCount;
        set => SetProperty(ref _overwrittenByBinaryCount, value);
    }

    [JsonIgnore]
    public string OverlayDetails
    {
        get => _overlayDetails;
        set => SetProperty(ref _overlayDetails, value);
    }

    [JsonIgnore]
    public IReadOnlyList<string> RelatedModIds
    {
        get => _relatedModIds;
        set => SetProperty(ref _relatedModIds, value ?? []);
    }

    [JsonIgnore]
    public bool IsConflictRelated
    {
        get => _isConflictRelated;
        set => SetProperty(ref _isConflictRelated, value);
    }

    [JsonIgnore]
    public bool HasOverlayInfo => ConflictKind is not ModConflictKind.None and not ModConflictKind.Disabled || ProvidesLaunchExecutable;

    [JsonIgnore]
    public string ConflictDisplay => ConflictKind switch
    {
        ModConflictKind.Overwrite => Strings.ModConflict_Overwrite,
        ModConflictKind.Overwritten => Strings.ModConflict_Overwritten,
        ModConflictKind.Mixed => Strings.ModConflict_Mixed,
        ModConflictKind.Redundant => Strings.ModConflict_Redundant,
        ModConflictKind.Disabled => Strings.ModConflict_Disabled,
        _ => Strings.ModConflict_None
    };

    [JsonIgnore]
    public string OverlaySummary
    {
        get
        {
            var parts = new List<string>();
            if (OverwrittenFileCount > 0)
            {
                parts.Add(LocalizedText.Format(Strings.ModConflict_OverwritesSummaryFormat, OverwrittenFileCount, OverwrittenModCount));
            }

            if (OverwrittenByFileCount > 0)
            {
                parts.Add(LocalizedText.Format(Strings.ModConflict_LosesSummaryFormat, OverwrittenByFileCount, OverwrittenByModCount));
            }

            if (ConflictKind == ModConflictKind.Redundant)
            {
                parts.Clear();
                parts.Add(Strings.ModConflict_FullyOverridden);
            }

            if (ProvidesLaunchExecutable)
            {
                parts.Add(Strings.ModConflict_Executable);
            }

            return string.Join(" · ", parts);
        }
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

}

public sealed record ModGroupKey(string Id, string Name, bool IsGroup)
{
    public static ModGroupKey Ungrouped(string modId) => new($"mod:{modId}", string.Empty, false);

    public static ModGroupKey Group(string firstModId, string name) => new($"group:{firstModId}", name, true);
}
