using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services.Fomod;

namespace StalkerModLauncher.ViewModels;

/// <summary>
/// Display option of a FOMOD group. Selection state lives in the owner
/// view-model dictionary so the visible page list can be rebuilt freely.
/// </summary>
public sealed class FomodInstallerOption : ObservableObject
{
    private readonly Action _changed;
    private readonly bool _isRequiredByGroup;
    private bool _isSelected;
    private bool _isEnabled = true;
    private FomodPluginType _effectiveType;

    internal FomodInstallerOption(
        string name,
        string description,
        string imagePath,
        string groupId,
        bool isSingleChoice,
        bool isRequiredByGroup,
        Action changed)
    {
        Name = name;
        Description = description;
        ImagePath = imagePath;
        GroupId = groupId;
        IsSingleChoice = isSingleChoice;
        _isRequiredByGroup = isRequiredByGroup;
        _changed = changed;
    }

    public string Name { get; }

    public string Description { get; }

    public string ImagePath { get; }

    public string GroupId { get; }

    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    public bool IsSingleChoice { get; }

    public bool IsMultiChoice => !IsSingleChoice;

    public FomodPluginType EffectiveType
    {
        get => _effectiveType;
        internal set
        {
            if (SetProperty(ref _effectiveType, value)) OnPropertyChanged(nameof(TypeText));
        }
    }

    public string TypeText => (_isRequiredByGroup && EffectiveType != FomodPluginType.NotUsable
        ? FomodPluginType.Required : EffectiveType) switch
    {
        FomodPluginType.Required => Strings.Fomod_Required,
        FomodPluginType.Recommended => Strings.Fomod_Recommended,
        FomodPluginType.NotUsable => Strings.Fomod_NotUsable,
        FomodPluginType.CouldBeUsable => Strings.Fomod_CouldBeUsable,
        _ => Strings.Fomod_Optional
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsEnabled && SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    internal void SetSelectedSilent(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}

public sealed class FomodInstallerGroup
{
    internal FomodInstallerGroup(string name, string groupId, FomodGroupType type, List<FomodInstallerOption> options)
    {
        Name = name;
        GroupId = groupId;
        Type = type;
        Options = options;
    }

    public string Name { get; }

    public string GroupId { get; }

    public FomodGroupType Type { get; }

    public string SelectionHint => Type switch
    {
        FomodGroupType.SelectExactlyOne => Strings.Fomod_ExactlyOne,
        FomodGroupType.SelectAtMostOne => Strings.Fomod_AtMostOne,
        FomodGroupType.SelectAtLeastOne => Strings.Fomod_AtLeastOne,
        FomodGroupType.SelectAll => Strings.Fomod_All,
        _ => Strings.Fomod_Any
    };

    public List<FomodInstallerOption> Options { get; }
}

public sealed class FomodInstallerStep
{
    internal FomodInstallerStep(string name, int originalIndex, List<FomodInstallerGroup> groups)
    {
        Name = name;
        OriginalIndex = originalIndex;
        Groups = groups;
    }

    public string Name { get; }

    internal int OriginalIndex { get; }

    public List<FomodInstallerGroup> Groups { get; }
}

/// <summary>
/// Shared FOMOD wizard view-model (no UI framework dependencies beyond binding).
/// Applies the same single-choice, flag propagation and visibility rules as
/// the non-interactive defaults, but driven by user selections.
/// </summary>
public sealed class FomodInstallerViewModel : ObservableObject, IDisposable
{
    private readonly FomodModule _module;
    private readonly Dictionary<(int Step, int Group, int Plugin), bool> _selected = new();
    private readonly Dictionary<(int Step, int Group, int Plugin), FomodPluginType> _effective = new();
    private readonly List<FomodInstallerStep> _allSteps = new();
    private int _currentStepIndex;
    private string? _validationError;
    private readonly Func<string, string>? _fileState;
    private readonly HashSet<(int Step, int Group)> _initialized = new();
    private readonly TaskCompletionSource<FomodSelector.Selection?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FomodInstallerOption? _previewOption;

    public FomodInstallerViewModel(FomodModule module, string packageRoot, Func<string, string>? fileState = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        _module = module;
        _fileState = fileState;
        PackageRoot = Path.GetFullPath(packageRoot);
        ModuleName = module.Name;

        var defaults = FomodSelector.CreateDefaults(module, fileState);
        BackCommand = new RelayCommand(MoveBack, () => CanGoBack);
        NextCommand = new RelayCommand(() => MoveNext(), () => CanGoNext);
        InstallCommand = new RelayCommand(Accept);
        CancelCommand = new RelayCommand(Dispose);
        foreach (var entry in defaults.Plugins)
        {
            foreach (var pluginIndex in entry.Value)
            {
                _selected[(entry.Key.Step, entry.Key.Group, pluginIndex)] = true;
            }
        }

        BuildDisplay();
        Refresh();
    }

    public event EventHandler? Completed;
    public Task<FomodSelector.Selection?> Completion => _completion.Task;
    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool HasChoices => VisibleSteps.Any(step => step.Groups.Any(group => group.Options.Any(option => option.IsEnabled))) || !CanFinish();
    public string ModuleImagePath => ResolveImage(_module.ImagePath);
    public FomodInstallerOption? PreviewOption
    {
        get => _previewOption;
        set => SetProperty(ref _previewOption, value);
    }

    private void Accept()
    {
        if (CanFinish() && _completion.TrySetResult(BuildSelection())) Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_completion.TrySetResult(null)) Completed?.Invoke(this, EventArgs.Empty);
    }

    public string ModuleName { get; }

    public string PackageRoot { get; }

    public List<FomodInstallerStep> VisibleSteps { get; } = new();

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            var clamped = VisibleSteps.Count == 0 ? 0 : Math.Clamp(value, 0, VisibleSteps.Count - 1);
            if (SetProperty(ref _currentStepIndex, clamped))
            {
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(StepCounterText));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(ShowInstall));
                BackCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                PreviewOption = CurrentStep?.Groups.SelectMany(group => group.Options).FirstOrDefault(option => option.IsSelected)
                    ?? CurrentStep?.Groups.SelectMany(group => group.Options).FirstOrDefault();
            }
        }
    }

    public FomodInstallerStep? CurrentStep =>
        VisibleSteps.Count == 0 ? null : VisibleSteps[CurrentStepIndex];

    public string StepCounterText =>
        VisibleSteps.Count == 0
            ? string.Empty
            : LocalizedText.Format(Strings.Fomod_StepFormat, CurrentStepIndex + 1, VisibleSteps.Count);

    public string? ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    public bool CanGoBack => CurrentStepIndex > 0;

    public bool CanGoNext => VisibleSteps.Count > 0 && CurrentStepIndex < VisibleSteps.Count - 1;

    public bool ShowInstall => VisibleSteps.Count == 0 || CurrentStepIndex == VisibleSteps.Count - 1;

    /// <summary>
    /// True when at least one option can actually be changed by the user.
    /// Modules without visible choices install silently with defaults.
    /// </summary>
    public static bool RequiresInteraction(FomodModule module, string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new FomodInstallerViewModel(module, packageRoot).VisibleSteps
            .Any(step => step.Groups
                .Any(group => group.Options.Any(option => option.IsEnabled)));
    }

    public bool MoveNext()
    {
        if (CurrentStep is null)
        {
            return false;
        }

        if (!ValidateStep(CurrentStep, out var error))
        {
            ValidationError = error;
            OnPropertyChanged(nameof(HasValidationError));
            return false;
        }

        ValidationError = null;
        OnPropertyChanged(nameof(HasValidationError));
        CurrentStepIndex++;
        return true;
    }

    public void MoveBack()
    {
        ValidationError = null;
        OnPropertyChanged(nameof(HasValidationError));
        CurrentStepIndex--;
    }

    public bool CanFinish()
    {
        foreach (var step in VisibleSteps)
        {
            if (!ValidateStep(step, out var error))
            {
                CurrentStepIndex = VisibleSteps.IndexOf(step);
                ValidationError = error;
                OnPropertyChanged(nameof(HasValidationError));
                return false;
            }
        }

        ValidationError = null;
        OnPropertyChanged(nameof(HasValidationError));
        return true;
    }

    public FomodSelector.Selection BuildSelection()
    {
        var selection = new FomodSelector.Selection { FileState = _fileState };
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var stepIndex = 0; stepIndex < _module.Steps.Count; stepIndex++)
        {
            var step = _module.Steps[stepIndex];
            if (step.Visible is not null && !FomodSelector.Evaluate(step.Visible, flags, _fileState))
            {
                continue;
            }

            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var chosen = new HashSet<int>();
                for (var pluginIndex = 0; pluginIndex < step.Groups[groupIndex].Plugins.Count; pluginIndex++)
                {
                    if (IsSelected(stepIndex, groupIndex, pluginIndex))
                    {
                        chosen.Add(pluginIndex);
                        foreach (var flag in step.Groups[groupIndex].Plugins[pluginIndex].Flags)
                        {
                            flags[flag.Key] = flag.Value;
                        }
                    }
                }

                if (chosen.Count > 0)
                {
                    selection.Plugins[(stepIndex, groupIndex)] = chosen;
                }
            }
        }

        foreach (var flag in flags)
        {
            selection.Flags[flag.Key] = flag.Value;
        }

        return selection;
    }

    internal void OnOptionChanged(int stepIndex, int groupIndex, int pluginIndex, bool isSelected)
    {
        var group = _module.Steps[stepIndex].Groups[groupIndex];
        ValidationError = null;
        OnPropertyChanged(nameof(HasValidationError));
        PreviewOption = GetOption(stepIndex, groupIndex, pluginIndex);
        if (isSelected && IsSingleChoice(group.Type))
        {
            for (var other = 0; other < group.Plugins.Count; other++)
            {
                if (other != pluginIndex && EffectiveTypeOf(stepIndex, groupIndex, other) != FomodPluginType.Required)
                {
                    _selected.Remove((stepIndex, groupIndex, other));
                }
            }
        }
        if (isSelected)
        {
            _selected[(stepIndex, groupIndex, pluginIndex)] = true;
        }
        else
        {
            _selected.Remove((stepIndex, groupIndex, pluginIndex));
        }

        Refresh();
    }

    private void BuildDisplay()
    {
        _allSteps.Clear();
        for (var stepIndex = 0; stepIndex < _module.Steps.Count; stepIndex++)
        {
            var step = _module.Steps[stepIndex];
            var groups = new List<FomodInstallerGroup>();
            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var group = step.Groups[groupIndex];
                var capturedGroup = groupIndex;
                var capturedStep = stepIndex;
                var options = new List<FomodInstallerOption>();
                for (var pluginIndex = 0; pluginIndex < group.Plugins.Count; pluginIndex++)
                {
                    var plugin = group.Plugins[pluginIndex];
                    var capturedPlugin = pluginIndex;
                    var imagePath = ResolveImage(plugin.ImagePath);
                    var groupId = $"s{capturedStep}g{capturedGroup}";
                    var option = new FomodInstallerOption(
                        plugin.Name,
                        plugin.Description,
                        imagePath,
                        groupId,
                        group.Type == FomodGroupType.SelectExactlyOne,
                        group.Type == FomodGroupType.SelectAll,
                        () => OnOptionChanged(capturedStep, capturedGroup, capturedPlugin, GetOption(capturedStep, capturedGroup, capturedPlugin)?.IsSelected == true));
                    options.Add(option);
                }

                groups.Add(new FomodInstallerGroup(
                    group.Name,
                    $"s{capturedStep}g{capturedGroup}",
                    group.Type,
                    options));
            }

            _allSteps.Add(new FomodInstallerStep(step.Name, stepIndex, groups));
        }
    }

    private FomodInstallerOption? GetOption(int stepIndex, int groupIndex, int pluginIndex)
    {
        var display = _allSteps.FirstOrDefault(step => step.OriginalIndex == stepIndex);
        var group = display?.Groups.ElementAtOrDefault(groupIndex);
        return group?.Options.ElementAtOrDefault(pluginIndex);
    }

    private void Refresh()
    {
        var currentOriginalIndex = CurrentStep?.OriginalIndex;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var visible = new List<int>();
        for (var stepIndex = 0; stepIndex < _module.Steps.Count; stepIndex++)
        {
            var step = _module.Steps[stepIndex];
            if (step.Visible is not null && !FomodSelector.Evaluate(step.Visible, flags, _fileState))
            {
                continue;
            }

            visible.Add(stepIndex);
            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var group = step.Groups[groupIndex];
                if (_initialized.Add((stepIndex, groupIndex)))
                {
                    foreach (var pluginIndex in FomodSelector.ChooseDefaults(group, flags, _fileState))
                        _selected[(stepIndex, groupIndex, pluginIndex)] = true;
                }
                var types = group.Plugins.Select(plugin => FomodSelector.EffectiveType(plugin, flags, _fileState)).ToArray();
                if (IsSingleChoice(group.Type) && Array.IndexOf(types, FomodPluginType.Required) is var requiredIndex && requiredIndex >= 0)
                {
                    for (var index = 0; index < types.Length; index++)
                        if (types[index] != FomodPluginType.Required) _selected.Remove((stepIndex, groupIndex, index));
                }
                for (var pluginIndex = 0; pluginIndex < group.Plugins.Count; pluginIndex++)
                {
                    var effective = types[pluginIndex];
                    _effective[(stepIndex, groupIndex, pluginIndex)] = effective;
                    var option = GetOption(stepIndex, groupIndex, pluginIndex);
                    if (option is not null)
                    {
                        option.EffectiveType = effective;
                    }

                    var enabled = effective != FomodPluginType.NotUsable &&
                                  group.Type != FomodGroupType.SelectAll &&
                                  effective != FomodPluginType.Required &&
                                  (!IsSingleChoice(group.Type) || !types.Contains(FomodPluginType.Required));
                    if (option is not null)
                    {
                        option.IsEnabled = enabled;
                    }

                    if (effective == FomodPluginType.Required || group.Type == FomodGroupType.SelectAll && effective != FomodPluginType.NotUsable)
                    {
                        _selected[(stepIndex, groupIndex, pluginIndex)] = true;
                    }
                    else if (effective == FomodPluginType.NotUsable)
                    {
                        _selected.Remove((stepIndex, groupIndex, pluginIndex));
                    }
                }

                foreach (var pluginIndex in _selected.Keys
                             .Where(key => key.Step == stepIndex && key.Group == groupIndex)
                             .Select(key => key.Plugin)
                             .Order()
                             .ToArray())
                {
                    foreach (var flag in group.Plugins[pluginIndex].Flags)
                    {
                        flags[flag.Key] = flag.Value;
                    }
                }
            }
        }

        var previous = VisibleSteps.Select(step => step.OriginalIndex).ToArray();
        if (!previous.SequenceEqual(visible))
        {
            VisibleSteps.Clear();
            foreach (var stepIndex in visible)
            {
                var display = _allSteps.First(step => step.OriginalIndex == stepIndex);
                VisibleSteps.Add(display);
            }

            var preservedIndex = VisibleSteps.FindIndex(step => step.OriginalIndex == currentOriginalIndex);
            CurrentStepIndex = preservedIndex >= 0 ? preservedIndex : Math.Min(CurrentStepIndex, Math.Max(0, VisibleSteps.Count - 1));
            OnPropertyChanged(nameof(CurrentStep));
            OnPropertyChanged(nameof(StepCounterText));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(ShowInstall));
            BackCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
        }

        SyncSelectionsToDisplay();
        PreviewOption ??= CurrentStep?.Groups.SelectMany(group => group.Options).FirstOrDefault(option => option.IsSelected)
            ?? CurrentStep?.Groups.SelectMany(group => group.Options).FirstOrDefault();
    }

    private void SyncSelectionsToDisplay()
    {
        foreach (var display in _allSteps)
        {
            foreach (var (groupIndex, group) in display.Groups.Select((group, index) => (index, group)))
            {
                foreach (var (pluginIndex, option) in group.Options.Select((option, index) => (index, option)))
                {
                    option.SetSelectedSilent(IsSelected(display.OriginalIndex, groupIndex, pluginIndex));
                }
            }
        }
    }

    private bool IsSelected(int stepIndex, int groupIndex, int pluginIndex) =>
        _selected.ContainsKey((stepIndex, groupIndex, pluginIndex));

    private FomodPluginType EffectiveTypeOf(int stepIndex, int groupIndex, int pluginIndex) =>
        _effective.TryGetValue((stepIndex, groupIndex, pluginIndex), out var effective)
            ? effective
            : _module.Steps[stepIndex].Groups[groupIndex].Plugins[pluginIndex].BaseType;

    private static bool IsSingleChoice(FomodGroupType type) =>
        type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne;

    private bool ValidateStep(FomodInstallerStep step, out string? error)
    {
        for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
        {
            var group = step.Groups[groupIndex];
            var source = _module.Steps[step.OriginalIndex].Groups[groupIndex];
            var chosen = Enumerable.Range(0, group.Options.Count)
                .Where(index => IsSelected(step.OriginalIndex, groupIndex, index)).ToHashSet();
            error = FomodSelector.ValidationError(source, group.Options.Select(option => option.EffectiveType).ToArray(), chosen);
            if (error is not null) return false;
        }

        error = null;
        return true;
    }

    private string ResolveImage(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        try
        {
            var normalized = FomodMaterializer.NormalizeRelative(relativePath);
            var full = Path.GetFullPath(Path.Combine(PackageRoot, normalized));
            if (!Services.FileSystemSafety.IsDirectoryInside(full, PackageRoot) || !File.Exists(full))
                return string.Empty;
            FomodMaterializer.EnsureNoLinks(PackageRoot, full);
            return full;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
