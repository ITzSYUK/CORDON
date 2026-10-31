using System.Collections.ObjectModel;
using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ProfileCreationViewModel : ObservableObject
{
    private readonly ModProfile _draft = new();
    private int _step = 1;
    private bool _useBaseGameData;
    private string _fsgameSourcePath = string.Empty;
    private string _name = Strings.Creation_DefaultName;
    private bool _isStandalone;
    private string _gamePath = string.Empty;
    private string _executableRelativePath = @"bin\xr_3da.exe";
    private string _executableSourcePath = string.Empty;
    private string _launchArguments = "-nointro";
    private string _message = Strings.Creation_Intro;
    private string _executableDetectionMessage = string.Empty;
    private bool _isMessageWarning;

    public ProfileCreationViewModel()
    {
        NextCommand = new RelayCommand(Next, () => Step < 3);
        BackCommand = new RelayCommand(Back, () => Step > 1);
        FinishCommand = new RelayCommand(Finish, () => Step == 3);
        BrowseGameCommand = new RelayCommand(BrowseGame);
        BrowseStandaloneCommand = new RelayCommand(BrowseStandalone);
        ScanModsCommand = new AsyncRelayCommand(ScanModsAsync);
        BrowseFsgameCommand = new RelayCommand(BrowseFsgame);
        ClearFsgameCommand = new RelayCommand(() => FsgameSourcePath = string.Empty);
        AddModCommand = new RelayCommand(AddMod, () => !IsStandalone || Mods.Count == 0);
        RemoveModCommand = new RelayCommand(parameter => RemoveMod(parameter as ModEntry), parameter => parameter is ModEntry);
        BrowseExecutableCommand = new RelayCommand(BrowseExecutable);
        Mods.CollectionChanged += (_, _) => OnModListChanged();
    }

    public event EventHandler<ModProfile>? Completed;
    public event EventHandler<ModScanSelectionEventArgs>? ModScanSelectionRequested;

    public ObservableCollection<ModEntry> Mods { get; } = new();

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(IsStepOne));
                OnPropertyChanged(nameof(IsStepTwo));
                OnPropertyChanged(nameof(IsStepThree));
                OnPropertyChanged(nameof(IsNextVisible));
                OnPropertyChanged(nameof(IsFinishVisible));
                RaiseCommandStates();
            }
        }
    }

    public string StepTitle => Step switch
    {
        1 => Strings.Creation_StepType,
        2 => IsStandalone ? Strings.Profile_Standalone : Strings.Creation_StepSources,
        _ => Strings.Creation_StepLaunch
    };

    public bool IsStepOne => Step == 1;
    public bool IsStepTwo => Step == 2;
    public bool IsStepThree => Step == 3;
    public bool IsNextVisible => Step < 3;
    public bool IsFinishVisible => Step == 3;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsStandalone
    {
        get => _isStandalone;
        set
        {
            if (SetProperty(ref _isStandalone, value))
            {
                OnPropertyChanged(nameof(ProfileTypeLabel));
                OnPropertyChanged(nameof(ProfileTypeDescription));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(SourceStepDescription));
                OnPropertyChanged(nameof(SourceSummary));
                OnPropertyChanged(nameof(IsOverlay));
                OnPropertyChanged(nameof(StandalonePath));
                OnPropertyChanged(nameof(ModListHint));
                ((RelayCommand)AddModCommand).RaiseCanExecuteChanged();
                if (value && Mods.Count > 1)
                {
                    while (Mods.Count > 1)
                    {
                        Mods.RemoveAt(Mods.Count - 1);
                    }

                    OnPropertyChanged(nameof(SourceSummary));
                }
            }
        }
    }

    public bool IsOverlay
    {
        get => !IsStandalone;
        set
        {
            if (value)
            {
                IsStandalone = false;
            }
        }
    }

    public string StandalonePath => Mods.FirstOrDefault()?.SourcePath ?? string.Empty;

    public bool HasMods => Mods.Count > 0;

    public bool HasNoMods => !HasMods;

    public string ModListHint => IsStandalone
        ? Strings.Creation_StandaloneRootHint
        : Strings.Creation_ModListHint;

    public string ProfileTypeDescription => IsStandalone
        ? Strings.Creation_StandaloneDescription
        : Strings.Creation_RegularDescription;

    public string ProfileTypeLabel => IsStandalone
        ? Strings.Profile_Standalone
        : Strings.Creation_ModdedGame;

    public string SourceStepDescription => IsStandalone
        ? Strings.Creation_StandaloneRootHint
        : Strings.Creation_RegularSourceHint;

    public string SourceSummary
    {
        get
        {
            if (IsStandalone)
            {
                return string.IsNullOrWhiteSpace(StandalonePath)
                    ? Strings.Creation_StandaloneNotSelected
                    : LocalizedText.Format(Strings.Creation_StandaloneSummaryFormat, StandalonePath);
            }

            var game = string.IsNullOrWhiteSpace(GamePath) ? Strings.Creation_NotSelected : GamePath;
            return LocalizedText.Format(Strings.Creation_RegularSummaryFormat, game, Mods.Count);
        }
    }

    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (SetProperty(ref _gamePath, value))
            {
                OnPropertyChanged(nameof(SourceSummary));
            }
        }
    }

    public string ExecutableRelativePath
    {
        get => _executableRelativePath;
        set
        {
            if (SetProperty(ref _executableRelativePath, value))
            {
                _executableSourcePath = string.Empty;
            }
        }
    }

    public string LaunchArguments
    {
        get => _launchArguments;
        set { if (SetProperty(ref _launchArguments, value)) RefreshSettings(); }
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsMessageWarning
    {
        get => _isMessageWarning;
        private set => SetProperty(ref _isMessageWarning, value);
    }

    public string ExecutableDetectionMessage
    {
        get => _executableDetectionMessage;
        private set => SetProperty(ref _executableDetectionMessage, value);
    }

    public bool UseBaseGameData
    {
        get => _useBaseGameData;
        set { if (SetProperty(ref _useBaseGameData, value)) { OnPropertyChanged(nameof(UseProfileGameData)); RefreshSettings(); } }
    }
    public bool UseProfileGameData { get => !UseBaseGameData; set { if (value) UseBaseGameData = false; } }
    public string FsgameSourcePath
    {
        get => _fsgameSourcePath;
        private set { if (SetProperty(ref _fsgameSourcePath, value)) RefreshSettings(); }
    }
    public bool HasManualFsgameSource => !string.IsNullOrWhiteSpace(FsgameSourcePath);
    private ModProfile SettingsProfile()
    {
        _draft.GameInstallPath = GamePath;
        _draft.IsStandalone = IsStandalone;
        _draft.UseBaseGameData = UseBaseGameData;
        _draft.FsgameSourcePath = FsgameSourcePath;
        _draft.LaunchArguments = LaunchArguments;
        _draft.Mods = Mods;
        _draft.WorkspacePath = Path.Combine(AppPaths.Current.GetPreferredWorkspaceRoot(GamePath), ProfileManager.CreateWorkspaceDirectoryName(_draft));
        return _draft;
    }
    public string GameDataDescription
    {
        get
        {
            if (IsStandalone) return Strings.SettingsProfile_StandaloneData;
            try { return ProfileDataPathResolver.GetGameDataRoot(SettingsProfile()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException) { return ex.Message; }
        }
    }
    public string FsgameSourceDisplay
    {
        get
        {
            try
            {
                var path = FileLayerPlan.ResolveFsgameSource(SettingsProfile())?.FullPath ?? Strings.Creation_NotSelected;
                return LocalizedText.Format(HasManualFsgameSource ? Strings.SettingsProfile_FsgameManualFormat : Strings.Creation_FsgameAutoFormat, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException) { return ex.Message; }
        }
    }
    private void RefreshSettings() { OnPropertyChanged(nameof(GameDataDescription)); OnPropertyChanged(nameof(FsgameSourceDisplay)); OnPropertyChanged(nameof(HasManualFsgameSource)); }
    public ICommand ScanModsCommand { get; }
    public ICommand BrowseFsgameCommand { get; }
    public ICommand ClearFsgameCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand BrowseStandaloneCommand { get; }
    public ICommand AddModCommand { get; }
    public ICommand RemoveModCommand { get; }
    public ICommand BrowseExecutableCommand { get; }

    public void SetDroppedGamePath(string path)
    {
        if (IsStandalone || !Directory.Exists(path))
        {
            return;
        }

        GamePath = Path.GetFullPath(path);
        ExecutableDetectionMessage = string.Empty;
    }

    public void SetDroppedStandalonePath(string path)
    {
        if (!IsStandalone || !Directory.Exists(path))
        {
            return;
        }

        Mods.Clear();
        AddModPath(path);
    }

    public void AddDroppedMods(IEnumerable<string> paths)
    {
        if (IsStandalone)
        {
            return;
        }

        foreach (var path in paths.Where(Directory.Exists))
        {
            AddModPath(path);
        }
    }

    private void Next()
    {
        if (!ValidateCurrentStep())
        {
            return;
        }

        Step++;
        SetMessage(Step == 2
            ? SourceStepDescription
            : Strings.Creation_ReviewExe);
        RefreshSettings();

        if (Step == 3)
        {
            AutoDetectExecutable();
        }
    }

    private void Back()
    {
        Step--;
        SetMessage(Step == 1
            ? Strings.Creation_Intro
            : SourceStepDescription);
    }

    private void Finish()
    {
        if (!ValidateCurrentStep())
        {
            return;
        }

        var profile = new ModProfile
        {
            Id = _draft.Id,
            UseBaseGameData = !IsStandalone && UseBaseGameData,
            FsgameSourcePath = IsStandalone ? string.Empty : FsgameSourcePath,
            Name = Name.Trim(),
            Description = IsStandalone ? Strings.Profile_Standalone : Strings.Creation_RegularProfileDescription,
            IsStandalone = IsStandalone,
            LaunchBackendKind = IsStandalone
                ? LaunchBackendKind.Standalone
                : LaunchBackendKind.VirtualFileSystem,
            GameInstallPath = IsStandalone ? string.Empty : GamePath.Trim(),
            ExecutableRelativePath = ExecutableRelativePath.Trim(),
            ExecutableSourcePath = IsStandalone ? string.Empty : _executableSourcePath,
            LaunchArguments = LaunchArguments.Trim()
        };

        foreach (var mod in Mods)
        {
            profile.Mods.Add(new ModEntry
            {
                Name = mod.Name,
                SourcePath = mod.SourcePath,
                IsEnabled = true,
                Order = profile.Mods.Count + 1
            });
        }

        Completed?.Invoke(this, profile);
    }

    private bool ValidateCurrentStep()
    {
        if (Step == 1 && string.IsNullOrWhiteSpace(Name))
        {
            SetMessage(Strings.Creation_NameRequired, isWarning: true);
            return false;
        }

        if (Step == 2)
        {
            if (!IsStandalone && !Directory.Exists(GamePath))
            {
                SetMessage(Strings.Creation_GameRequired, isWarning: true);
                return false;
            }

            if (IsStandalone && (Mods.Count != 1 || !Directory.Exists(Mods[0].SourcePath)))
            {
                SetMessage(Strings.Creation_StandaloneRequired, isWarning: true);
                return false;
            }
        }

        if (Step == 3)
        {
            try
            {
                FileSystemSafety.EnsureRelativePath(ExecutableRelativePath, Strings.Creation_ExecutableField);
                if (FindExactExecutableSource(CreateExecutableSearchRoots().ToArray(), ExecutableRelativePath) is null)
                {
                    SetMessage(Strings.Creation_ExecutableMissing, isWarning: true);
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, isWarning: true);
                return false;
            }
        }

        if (Step == 3 && !IsStandalone)
        {
            try
            {
                var profile = SettingsProfile();
                if (!string.IsNullOrWhiteSpace(FsgameSourcePath))
                {
                    var source = FileLayerPlan.ResolveFsgameSource(profile);
                    if (source is null) throw new InvalidDataException(Strings.SettingsProfile_FsgameLocation);
                    ProfileDataConfigurator.ValidateFsgameSource(source.FullPath);
                }
                if (UseBaseGameData) _ = ProfileDataPathResolver.GetGameDataRoot(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
            {
                SetMessage(ex.Message, isWarning: true);
                return false;
            }
        }

        return true;
    }

    private async Task ScanModsAsync()
    {
        var folder = DialogService.PickFolder(Strings.Mod_ScanPickFolder);
        if (folder is not null) await ScanModsFromFolderAsync(folder);
    }

    internal async Task ScanModsFromFolderAsync(string folder)
    {
        try
        {
            var discovered = await ModScannerService.ScanFolderAsync(folder);
            if (discovered.Count == 0) { new DialogService().ShowError(Strings.Mod_NotFoundTitle, Strings.Mod_NotFound); return; }
            var mods = discovered.Select(SelectableMod.FromDiscovered).ToList();
            IReadOnlyList<SelectableMod>? selected;
            if (ModScanSelectionRequested is not null)
            {
                var request = new ModScanSelectionEventArgs(mods);
                ModScanSelectionRequested.Invoke(this, request);
                selected = await request.Completion;
            }
            else
            {
                var window = new Views.ScanResultsWindow();
                foreach (var mod in mods) window.Mods.Add(mod);
                selected = window.ShowDialog() == true ? window.GetSelectedMods() : null;
            }
            if (selected is not null) foreach (var mod in selected) AddModPath(mod.Path);
        }
        catch (Exception ex) { new DialogService().ShowError(Strings.Mod_ScanFailed, ex.Message); }
    }

    private void BrowseFsgame()
    {
        var selected = DialogService.PickFile(Strings.SettingsProfile_PickFsgame, Strings.Dialog_LtxFilter, GamePath);
        if (selected is null) return;
        if (!Path.GetExtension(selected).Equals(".ltx", StringComparison.OrdinalIgnoreCase) ||
            !new[] { GamePath }.Concat(Mods.Select(mod => mod.SourcePath)).Any(root => Directory.Exists(root) && FileSystemSafety.IsDirectoryInside(selected, root)))
        {
            new DialogService().ShowError(Strings.SettingsProfile_InvalidFsgame, Strings.SettingsProfile_FsgameLocation);
            return;
        }
        FsgameSourcePath = Path.GetFullPath(selected);
    }

    private void BrowseGame()
    {
        var selected = DialogService.PickFolder(Strings.Common_ChooseBaseGame, GamePath);
        if (selected is not null)
        {
            GamePath = selected;
            ExecutableDetectionMessage = string.Empty;
        }
    }

    private void BrowseStandalone()
    {
        var selected = DialogService.PickFolder(Strings.Creation_PickStandalone, StandalonePath);
        if (selected is null)
        {
            return;
        }

        Mods.Clear();
        AddModPath(selected);
    }

    private void AddMod()
    {
        var selected = DialogService.PickFolder(IsStandalone ? Strings.Creation_StandalonePrompt : Strings.Creation_PickMod);
        if (selected is null)
        {
            return;
        }

        AddModPath(selected);
    }

    private void AddModPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) ||
            Mods.Any(mod => FileSystemSafety.IsSameDirectory(mod.SourcePath, fullPath)))
        {
            return;
        }

        Mods.Add(new ModEntry
        {
            Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            SourcePath = fullPath,
            Order = Mods.Count + 1
        });
        ExecutableDetectionMessage = string.Empty;
        OnPropertyChanged(nameof(StandalonePath));
        OnPropertyChanged(nameof(SourceSummary));
        ((RelayCommand)AddModCommand).RaiseCanExecuteChanged();
    }

    private void RemoveMod(ModEntry? mod)
    {
        if (mod is null)
        {
            return;
        }

        Mods.Remove(mod);
        for (var index = 0; index < Mods.Count; index++)
        {
            Mods[index].Order = index + 1;
        }
        OnPropertyChanged(nameof(StandalonePath));
        OnPropertyChanged(nameof(SourceSummary));
        ExecutableDetectionMessage = string.Empty;
        ((RelayCommand)AddModCommand).RaiseCanExecuteChanged();
    }

    private void BrowseExecutable()
    {
        var initial = Mods.LastOrDefault()?.SourcePath;
        if (!Directory.Exists(initial))
        {
            initial = GamePath;
        }

        var selected = DialogService.PickExecutable(Strings.Creation_PickExecutable, initial);
        if (selected is null)
        {
            return;
        }

        foreach (var root in CreateExecutableSearchRoots().Where(root => Directory.Exists(root.RootPath)))
        {
            var relative = Path.GetRelativePath(root.RootPath, selected);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                SetExecutableSelection(relative, root.RootPath, root.DisplayName);
                return;
            }
        }

        SetMessage(Strings.Creation_ExecutableOutside, isWarning: true);
    }

    private void AutoDetectExecutable()
    {
        var roots = CreateExecutableSearchRoots().ToArray();
        var currentExact = FindExactExecutableSource(roots, ExecutableRelativePath);
        if (!ExecutableRelativePath.Equals(@"bin\xr_3da.exe", StringComparison.OrdinalIgnoreCase) &&
            currentExact is not null)
        {
            ExecutableDetectionMessage =
                LocalizedText.Format(Strings.Creation_UsingPathFormat, ExecutableRelativePath, currentExact.Value.SourceName);
            return;
        }

        var detected = LaunchExecutableDetector.DetectBest(roots, requestedRelativePath: null);
        if (detected is null)
        {
            ExecutableDetectionMessage = Strings.Creation_ExecutableNotDetected;
            return;
        }

        ExecutableRelativePath = detected.RelativePath;
        _executableSourcePath = string.Empty;
        ExecutableDetectionMessage = LocalizedText.Format(Strings.Creation_AutoSelectedFormat, detected.Summary);
    }

    private void SetExecutableSelection(string relativePath, string sourceRootPath, string sourceName)
    {
        _executableRelativePath = relativePath;
        _executableSourcePath = IsStandalone ? string.Empty : Path.GetFullPath(sourceRootPath);
        OnPropertyChanged(nameof(ExecutableRelativePath));
        ExecutableDetectionMessage = IsStandalone
            ? LocalizedText.Format(Strings.Creation_ManualSelectedFormat, relativePath)
            : LocalizedText.Format(Strings.Creation_ManualSelectedSourceFormat, relativePath, sourceName);
    }

    private IEnumerable<LaunchExecutableSearchRoot> CreateExecutableSearchRoots()
    {
        if (!IsStandalone && Directory.Exists(GamePath))
        {
            yield return new LaunchExecutableSearchRoot(GamePath, Strings.Creation_BaseGameSource, 0, IsBaseGameRoot: true);
        }

        foreach (var mod in Mods.OrderBy(mod => mod.Order).Where(mod => Directory.Exists(mod.SourcePath)))
        {
            yield return new LaunchExecutableSearchRoot(
                mod.SourcePath,
                LocalizedText.Format(Strings.Creation_ModSourceFormat, mod.Name),
                mod.Order,
                IsBaseGameRoot: IsStandalone);
        }
    }

    private static (string FullPath, string SourceName)? FindExactExecutableSource(
        IReadOnlyList<LaunchExecutableSearchRoot> roots,
        string relativePath)
    {
        try
        {
            FileSystemSafety.EnsureRelativePath(relativePath, Strings.Creation_ExecutableField);
        }
        catch
        {
            return null;
        }

        return roots
            .Where(root => Directory.Exists(root.RootPath))
            .Select(root => new
            {
                FullPath = Path.Combine(root.RootPath, relativePath),
                root.DisplayName,
                root.Order
            })
            .Where(candidate => File.Exists(candidate.FullPath))
            .OrderByDescending(candidate => candidate.Order)
            .Select(candidate => ((string FullPath, string SourceName)?)(candidate.FullPath, candidate.DisplayName))
            .FirstOrDefault();
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)NextCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BackCommand).RaiseCanExecuteChanged();
        ((RelayCommand)FinishCommand).RaiseCanExecuteChanged();
    }

    private void SetMessage(string message, bool isWarning = false)
    {
        Message = message;
        IsMessageWarning = isWarning;
    }

    private void OnModListChanged()
    {
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(StandalonePath));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(ModListHint));
        ((RelayCommand)AddModCommand).RaiseCanExecuteChanged();
    }
}
