using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Windows.Data;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProfileFileStateCacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConflictAnalysisDelay = TimeSpan.FromMilliseconds(100);
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly LaunchCoordinator _launchCoordinator;
    private readonly DialogService _dialogService;
    private readonly ModConflictAnalyzer _modConflictAnalyzer;
    private readonly ProfileManager _profileManager;
    private readonly LaunchPreflightService _launchPreflightService;
    private readonly ApplicationLogService _applicationLogService;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly DebouncedAsyncAction _autoSave;
    private readonly DebouncedAsyncAction _conflictAnalysisDebounce;
    private readonly Dictionary<ModProfile, ListCollectionView> _filteredModViews =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModProfile, (ValidationResult Result, DateTime CreatedAtUtc)> _validationCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ModProfile> _trackedProfiles = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ModProfile> _profilesRenumberingMods = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModProfile, ObservableCollection<ModEntry>> _trackedModCollections =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ObservableCollection<ModEntry>, ModProfile> _modCollectionOwners =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModEntry, ModProfile> _modOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModProfile, DateTime> _automaticExecutableRefreshTimes =
        new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _conflictAnalysisCancellation;
    private string _lastBrowsedGamePath = string.Empty;
    private ModProfile? _selectedProfile;
    private ModEntry? _selectedMod;
    private string _validationSummary = "Выберите папку с установленной игрой.";
    private bool _isGameValid;
    private bool _isBuilding;
    private bool _isPdaInterfaceEnabled;
    private bool _useNewPdaInterface;
    private string _buildProgressText = string.Empty;
    private bool _isInstallingModArchive;
    private bool _isModArchiveInstallProgressIndeterminate;
    private double _modArchiveInstallProgress;
    private string _modArchiveInstallProgressText = string.Empty;
    private bool _isModArchiveInstallDestinationConflict;
    private string _modArchiveInstallDestinationConflictText = string.Empty;
    private string _modArchiveInstallDestinationConflictAction = string.Empty;
    private TaskCompletionSource<bool>? _modArchiveInstallDestinationChoice;
    private bool _isModArchiveInstallCompleted;
    private string _installedModArchiveName = string.Empty;
    private string _installedModArchivePath = string.Empty;
    private string _installedModArchiveDetails = string.Empty;
    private ICollectionView? _filteredMods;
    private string _modSearchText = string.Empty;
    private ModListFilter _selectedModFilter;
    private bool _disposed;

    public MainViewModel(
        AppPaths paths,
        SettingsStore settingsStore,
        LaunchCoordinator launchCoordinator,
        DialogService dialogService,
        ModConflictAnalyzer modConflictAnalyzer,
        ProfileManager profileManager,
        LaunchPreflightService launchPreflightService,
        ApplicationLogService applicationLogService,
        IStartupRegistrationService? startupRegistrationService = null)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _launchCoordinator = launchCoordinator;
        _dialogService = dialogService;
        _modConflictAnalyzer = modConflictAnalyzer;
        _profileManager = profileManager;
        _launchPreflightService = launchPreflightService;
        _applicationLogService = applicationLogService;
        _startupRegistrationService = startupRegistrationService ?? new StartupRegistrationService();
        _autoSave = new DebouncedAsyncAction(SaveAsync, TimeSpan.FromMilliseconds(500));
        _conflictAnalysisDebounce = new DebouncedAsyncAction(
            () => InvokeOnUiAsync(CalculateModOverlayInfo),
            ConflictAnalysisDelay);
        ActivityLog = new ActivityLogViewModel(_applicationLogService, _autoSave.Schedule);

        Profiles.CollectionChanged += ProfilesOnCollectionChanged;
        _settingsStore.RecoveryCompleted += SettingsStoreOnRecoveryCompleted;

        ChooseGameFolderCommand = new RelayCommand(ChooseGameFolder);
        NewProfileCommand = new RelayCommand(NewProfile);
        ImportSettingsCommand = new AsyncRelayCommand(ImportSettingsAsync, () => CanImportSettings);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        InlineDuplicateProfileCommand = new RelayCommand(
            parameter => DuplicateProfile(parameter as ModProfile),
            parameter => parameter is ModProfile);
        InlineExportProfileCommand = new RelayCommand(
            parameter => ExportProfile(parameter as ModProfile),
            parameter => parameter is ModProfile);
        InlineDeleteProfileCommand = new RelayCommand(
            parameter => DeleteProfile(parameter as ModProfile),
            parameter => parameter is ModProfile { IsRunning: false });
        BrowseExecutableCommand = new RelayCommand(BrowseExecutable, () => SelectedProfile is not null);
        AddModCommand = new RelayCommand(AddMod, CanAddMod);
        InstallModArchiveCommand = new AsyncRelayCommand(InstallModArchiveAsync, CanInstallModArchive);
        DismissModArchiveInstallCompletedCommand = new RelayCommand(() => IsModArchiveInstallCompleted = false);
        ContinueModArchiveInstallCommand = new RelayCommand(() => ResolveModArchiveInstallDestinationChoice(true));
        CancelModArchiveInstallCommand = new RelayCommand(() => ResolveModArchiveInstallDestinationChoice(false));
        RemoveModCommand = new RelayCommand(RemoveMod, () => CanEditSelectedProfile && SelectedMod is not null);
        MoveModUpCommand = new RelayCommand(() => MoveSelectedMod(-1), () => CanMoveSelectedMod(-1));
        MoveModDownCommand = new RelayCommand(() => MoveSelectedMod(1), () => CanMoveSelectedMod(1));
        InlineRemoveModCommand = new RelayCommand(
            parameter => RemoveInlineMod(parameter as ModEntry),
            parameter => CanEditSelectedProfile && parameter is ModEntry);
        InlineMoveModUpCommand = new RelayCommand(
            parameter => MoveInlineMod(parameter as ModEntry, -1),
            parameter => CanMoveInlineMod(parameter as ModEntry, -1));
        InlineMoveModDownCommand = new RelayCommand(
            parameter => MoveInlineMod(parameter as ModEntry, 1),
            parameter => CanMoveInlineMod(parameter as ModEntry, 1));
        InlineOpenModFolderCommand = new RelayCommand(
            parameter => OpenInlineModFolder(parameter as ModEntry),
            parameter => parameter is ModEntry mod && Directory.Exists(mod.SourcePath));
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, CanLaunch);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        OpenProfileFolderCommand = new RelayCommand(OpenProfileFolder, () => SelectedProfile is not null);
        OpenSelectedModFolderCommand = new RelayCommand(OpenSelectedModFolder, () => SelectedMod is not null);
        ExportProfileCommand = new RelayCommand(ExportProfile, () => SelectedProfile is not null);
        ImportProfileCommand = new RelayCommand(ImportProfile);
        ImportMo2CollectionCommand = new RelayCommand(() => Mo2ImportRequested?.Invoke(this, EventArgs.Empty));
        ShowSelectedModConflictsCommand = new RelayCommand(
            parameter => ConflictExplorerRequested?.Invoke(this, parameter as ModEntry ?? SelectedMod),
            parameter => SelectedProfile is { IsStandalone: false } && (parameter is ModEntry || SelectedMod is not null));
        ShowFileTreeCommand = new RelayCommand(
            () => ConflictExplorerRequested?.Invoke(this, SelectedMod),
            () => SelectedProfile is { IsStandalone: false });
        ScanForModsCommand = new AsyncRelayCommand(
            ScanForModsAsync,
            () => CanEditSelectedProfile && SelectedProfile is { IsStandalone: false });
        Initialization = LoadAsync();
    }

    public ObservableCollection<ModProfile> Profiles { get; } = new();

    public ActivityLogViewModel ActivityLog { get; }

    public Task Initialization { get; }

    internal DialogService DialogService => _dialogService;

    public bool HasProfiles => Profiles.Count > 0;

    public bool CanImportSettings => _paths.IsPortable && !HasProfiles;

    public bool IsPdaInterfaceEnabled
    {
        get => _isPdaInterfaceEnabled;
        set
        {
            if (SetProperty(ref _isPdaInterfaceEnabled, value))
            {
                _autoSave.Schedule();
            }
        }
    }

    public bool UseNewPdaInterface => _useNewPdaInterface;

    public event EventHandler? ProfileCreationRequested;
    public event EventHandler? Mo2ImportRequested;
    public event EventHandler<ModScanSelectionEventArgs>? ModScanSelectionRequested;
    public event EventHandler<ModEntry?>? ConflictExplorerRequested;

    public string GameInstallPath
    {
        get => SelectedProfile?.GameInstallPath ?? _lastBrowsedGamePath;
        set
        {
            if (SelectedProfile is not null)
            {
                if (SelectedProfile.GameInstallPath != value)
                {
                    SelectedProfile.GameInstallPath = value;
                    OnPropertyChanged(nameof(GameInstallPath));
                    RefreshValidation();
                    _autoSave.Schedule();
                }
            }
            else
            {
                _lastBrowsedGamePath = value;
                OnPropertyChanged(nameof(GameInstallPath));
                RefreshValidation();
                _autoSave.Schedule();
            }
        }
    }

    public ModProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            var oldProfile = _selectedProfile;
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            if (oldProfile is not null)
            {
                oldProfile.PropertyChanged -= OnSelectedProfilePropertyChanged;
            }

            SelectedMod = null;
            IsModArchiveInstallCompleted = false;
            ResolveModArchiveInstallDestinationChoice(false);
            IsModArchiveInstallDestinationConflict = false;
            CreateFilteredModsView();
            RecalculateModOverlayInfo();
            RefreshValidation();
            RaiseCommandStates();
            OnPropertyChanged(nameof(GameInstallPath));
            OnPropertyChanged(nameof(CanEditSelectedProfile));

            if (_selectedProfile is not null)
            {
                _selectedProfile.PropertyChanged += OnSelectedProfilePropertyChanged;
                if (!_automaticExecutableRefreshTimes.TryGetValue(_selectedProfile, out var refreshedAtUtc) ||
                    DateTime.UtcNow - refreshedAtUtc >= ProfileFileStateCacheLifetime)
                {
                    _automaticExecutableRefreshTimes[_selectedProfile] = DateTime.UtcNow;
                    RefreshAutomaticExecutableSelection(
                        _selectedProfile,
                        "выбора профиля",
                        preferExistingRelativePath: true);
                }
            }
        }
    }

    private void OnSelectedProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModProfile.IsStandalone))
        {
            if (SelectedProfile is { IsStandalone: true })
            {
                AutoDetectStandaloneExecutable();
            }

            RaiseCommandStates();
            _autoSave.Schedule();
        }

        if (e.PropertyName == nameof(ModProfile.ExecutableRelativePath) ||
            e.PropertyName == nameof(ModProfile.ExecutableSourcePath))
        {
            RecalculateModOverlayInfo();
        }
    }

    public ModEntry? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                UpdateRelatedModHighlights();
                RaiseCommandStates();
            }
        }
    }

    public ICollectionView? FilteredMods
    {
        get => _filteredMods;
        private set => SetProperty(ref _filteredMods, value);
    }

    public IReadOnlyList<ModListFilterOption> ModFilterOptions { get; } =
    [
        new(ModListFilter.All, "Все моды"),
        new(ModListFilter.Conflicts, "Конфликтующие"),
        new(ModListFilter.Overwrite, "Перезаписывающие"),
        new(ModListFilter.Overwritten, "Перезаписанные"),
        new(ModListFilter.Mixed, "Смешанные"),
        new(ModListFilter.Redundant, "Полностью перекрытые"),
        new(ModListFilter.Binaries, "EXE и DLL")
    ];

    public string ModSearchText
    {
        get => _modSearchText;
        set
        {
            if (SetProperty(ref _modSearchText, value))
            {
                FilteredMods?.Refresh();
            }
        }
    }

    public ModListFilter SelectedModFilter
    {
        get => _selectedModFilter;
        set
        {
            if (SetProperty(ref _selectedModFilter, value))
            {
                FilteredMods?.Refresh();
            }
        }
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public bool IsGameValid
    {
        get => _isGameValid;
        private set => SetProperty(ref _isGameValid, value);
    }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set => SetProperty(ref _isBuilding, value);
    }

    public bool CanEditSelectedProfile => SelectedProfile is { IsRunning: false };

    public string BuildProgressText
    {
        get => _buildProgressText;
        private set => SetProperty(ref _buildProgressText, value);
    }

    public bool IsInstallingModArchive
    {
        get => _isInstallingModArchive;
        private set => SetProperty(ref _isInstallingModArchive, value);
    }

    public bool IsModArchiveInstallProgressIndeterminate
    {
        get => _isModArchiveInstallProgressIndeterminate;
        private set => SetProperty(ref _isModArchiveInstallProgressIndeterminate, value);
    }

    public double ModArchiveInstallProgress
    {
        get => _modArchiveInstallProgress;
        private set => SetProperty(ref _modArchiveInstallProgress, value);
    }

    public string ModArchiveInstallProgressText
    {
        get => _modArchiveInstallProgressText;
        private set => SetProperty(ref _modArchiveInstallProgressText, value);
    }

    public bool IsModArchiveInstallDestinationConflict
    {
        get => _isModArchiveInstallDestinationConflict;
        private set => SetProperty(ref _isModArchiveInstallDestinationConflict, value);
    }

    public string ModArchiveInstallDestinationConflictText
    {
        get => _modArchiveInstallDestinationConflictText;
        private set => SetProperty(ref _modArchiveInstallDestinationConflictText, value);
    }

    public string ModArchiveInstallDestinationConflictAction
    {
        get => _modArchiveInstallDestinationConflictAction;
        private set => SetProperty(ref _modArchiveInstallDestinationConflictAction, value);
    }

    public bool IsModArchiveInstallCompleted
    {
        get => _isModArchiveInstallCompleted;
        private set => SetProperty(ref _isModArchiveInstallCompleted, value);
    }

    public string InstalledModArchiveName
    {
        get => _installedModArchiveName;
        private set => SetProperty(ref _installedModArchiveName, value);
    }

    public string InstalledModArchivePath
    {
        get => _installedModArchivePath;
        private set => SetProperty(ref _installedModArchivePath, value);
    }

    public string InstalledModArchiveDetails
    {
        get => _installedModArchiveDetails;
        private set => SetProperty(ref _installedModArchiveDetails, value);
    }

    public RelayCommand ChooseGameFolderCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand ImportSettingsCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand InlineDuplicateProfileCommand { get; }
    public RelayCommand InlineExportProfileCommand { get; }
    public RelayCommand InlineDeleteProfileCommand { get; }
    public RelayCommand BrowseExecutableCommand { get; }
    public RelayCommand AddModCommand { get; }
    public AsyncRelayCommand InstallModArchiveCommand { get; }
    public RelayCommand DismissModArchiveInstallCompletedCommand { get; }
    public RelayCommand ContinueModArchiveInstallCommand { get; }
    public RelayCommand CancelModArchiveInstallCommand { get; }
    public RelayCommand RemoveModCommand { get; }
    public RelayCommand MoveModUpCommand { get; }
    public RelayCommand MoveModDownCommand { get; }
    public RelayCommand InlineRemoveModCommand { get; }
    public RelayCommand InlineMoveModUpCommand { get; }
    public RelayCommand InlineMoveModDownCommand { get; }
    public RelayCommand InlineOpenModFolderCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand OpenProfileFolderCommand { get; }
    public RelayCommand OpenSelectedModFolderCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }
    public RelayCommand ImportMo2CollectionCommand { get; }
    public RelayCommand ShowSelectedModConflictsCommand { get; }
    public RelayCommand ShowFileTreeCommand { get; }
    public AsyncRelayCommand ScanForModsCommand { get; }
    public void AppendLog(string message, LauncherLogLevel level = LauncherLogLevel.Standard) => Log(message, level);

    private void RefreshValidation()
    {
        var profile = SelectedProfile;
        ValidationResult result;
        if (profile is null)
        {
            result = ProfileReadinessService.Validate(null);
        }
        else
        {
            result = GetProfileValidation(profile);
        }

        IsGameValid = result.IsValid;
        ValidationSummary = result.Summary;
        RaiseCommandStates();
    }

    private ValidationResult GetProfileValidation(ModProfile profile, bool forceRefresh = false)
    {
        ValidationResult result;
        if (!forceRefresh &&
            _validationCache.TryGetValue(profile, out var cached) &&
            DateTime.UtcNow - cached.CreatedAtUtc < ProfileFileStateCacheLifetime)
        {
            result = cached.Result;
        }
        else
        {
            result = ProfileReadinessService.Validate(profile);
            _validationCache[profile] = (result, DateTime.UtcNow);
        }

        profile.HasLaunchError = !result.IsValid;
        profile.LaunchErrorSummary = result.IsValid ? string.Empty : result.Summary;
        return result;
    }

    public void RefreshProfileLaunchReadiness(bool forceRefresh = false)
    {
        foreach (var profile in Profiles)
        {
            _ = GetProfileValidation(profile, forceRefresh);
        }
    }

    private void RaiseCommandStates()
    {
        DeleteProfileCommand.RaiseCanExecuteChanged();
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        InlineDuplicateProfileCommand.RaiseCanExecuteChanged();
        InlineExportProfileCommand.RaiseCanExecuteChanged();
        InlineDeleteProfileCommand.RaiseCanExecuteChanged();
        BrowseExecutableCommand.RaiseCanExecuteChanged();
        AddModCommand.RaiseCanExecuteChanged();
        InstallModArchiveCommand.RaiseCanExecuteChanged();
        RemoveModCommand.RaiseCanExecuteChanged();
        MoveModUpCommand.RaiseCanExecuteChanged();
        MoveModDownCommand.RaiseCanExecuteChanged();
        InlineRemoveModCommand.RaiseCanExecuteChanged();
        InlineMoveModUpCommand.RaiseCanExecuteChanged();
        InlineMoveModDownCommand.RaiseCanExecuteChanged();
        InlineOpenModFolderCommand.RaiseCanExecuteChanged();
        LaunchCommand.RaiseCanExecuteChanged();
        OpenProfileFolderCommand.RaiseCanExecuteChanged();
        OpenSelectedModFolderCommand.RaiseCanExecuteChanged();
        ExportProfileCommand.RaiseCanExecuteChanged();
        ImportProfileCommand.RaiseCanExecuteChanged();
        ImportSettingsCommand.RaiseCanExecuteChanged();
        ImportMo2CollectionCommand.RaiseCanExecuteChanged();
        ShowSelectedModConflictsCommand.RaiseCanExecuteChanged();
        ShowFileTreeCommand.RaiseCanExecuteChanged();
        ScanForModsCommand.RaiseCanExecuteChanged();
    }

    private void Log(string message, LauncherLogLevel level = LauncherLogLevel.Standard)
    {
        var app = App.Current;
        if (app is null)
        {
            ActivityLog.Append(message, level);
            return;
        }

        app.Dispatcher.Invoke(() => ActivityLog.Append(message, level));
    }

    public ConflictExplorerViewModel CreateConflictExplorerViewModel(ModEntry? selectedMod)
    {
        if (SelectedProfile is not { IsStandalone: false } profile)
        {
            throw new InvalidOperationException("Выберите обычный профиль.");
        }

        return new ConflictExplorerViewModel(
            profile,
            selectedMod,
            _modConflictAnalyzer,
            _dialogService,
            SaveOrThrowAsync,
            () =>
            {
                ModListEditor.Renumber(profile);
                RecalculateModOverlayInfo();
                RefreshValidation();
                CreateFilteredModsView();
            });
    }

    private void CreateFilteredModsView()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            FilteredMods = null;
            return;
        }

        if (!_filteredModViews.TryGetValue(profile, out var view))
        {
            view = new ListCollectionView((IList)profile.Mods)
            {
                Filter = item => item is ModEntry mod && MatchesModFilter(mod)
            };
            _filteredModViews.Add(profile, view);
        }

        FilteredMods = view;
        view.Refresh();
    }

    private bool MatchesModFilter(ModEntry mod)
    {
        var searchMatches = string.IsNullOrWhiteSpace(ModSearchText) ||
                            mod.Name.Contains(ModSearchText, StringComparison.OrdinalIgnoreCase) ||
                            mod.SourcePath.Contains(ModSearchText, StringComparison.OrdinalIgnoreCase) ||
                            mod.GroupName.Contains(ModSearchText, StringComparison.OrdinalIgnoreCase);
        if (!searchMatches)
        {
            return false;
        }

        return SelectedModFilter switch
        {
            ModListFilter.Conflicts => mod.ConflictKind is not ModConflictKind.None and not ModConflictKind.Disabled,
            ModListFilter.Overwrite => mod.ConflictKind == ModConflictKind.Overwrite,
            ModListFilter.Overwritten => mod.ConflictKind == ModConflictKind.Overwritten,
            ModListFilter.Mixed => mod.ConflictKind == ModConflictKind.Mixed,
            ModListFilter.Redundant => mod.ConflictKind == ModConflictKind.Redundant,
            ModListFilter.Binaries => mod.OverwrittenBinaryCount > 0 || mod.OverwrittenByBinaryCount > 0 || mod.ProvidesLaunchExecutable,
            _ => true
        };
    }

    private void UpdateRelatedModHighlights()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var related = SelectedMod?.RelatedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in SelectedProfile.Mods)
        {
            mod.IsConflictRelated = related.Contains(mod.Id);
        }
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var app = App.Current;
        if (app is null)
        {
            action();
            return Task.CompletedTask;
        }

        return app.Dispatcher.InvokeAsync(action).Task;
    }
}
