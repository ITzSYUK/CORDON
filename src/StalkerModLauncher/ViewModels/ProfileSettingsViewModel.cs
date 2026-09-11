using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ProfileSettingsViewModel : ObservableObject
{
    private readonly ModProfile _profile;
    private readonly DialogService _dialogService;
    private readonly Func<Task> _onSave;
    private readonly Func<string, ProfileExecutableSelection?> _createExecutableSelection;
    private readonly Func<ProfileExecutableSelection?> _detectAutomaticExecutableSelection;
    private readonly Func<string, bool> _isNameTaken;
    private readonly bool _isUsvfsAvailable;
    private readonly string _defaultModInstallPath;
    private string _profileName;
    private string _profileDescription;
    private string _executableRelativePath;
    private string _executableSourcePath;
    private string _launchArguments;
    private string _workspacePath;
    private string _mo2OverwritePath;
    private string _modInstallPath;
    private bool _isEnabled;
    private bool _isDiscordStatusEnabled;
    private bool _isStandalone;
    private bool _useBaseGameData;
    private LaunchBackendKind _launchBackendKind;
    private string _usvfsExecutableOverrideRelativePath;
    private string _anomalyRenderer = string.Empty;
    private bool _anomalyUseAvx;
    private readonly bool _isAnomalyProfile;

    public ProfileSettingsViewModel(
        ModProfile profile,
        DialogService dialogService,
        Func<Task> onSave,
        Func<string, ProfileExecutableSelection?> createExecutableSelection,
        Func<ProfileExecutableSelection?> detectAutomaticExecutableSelection,
        Func<string, bool> isNameTaken,
        bool? usvfsAvailable = null,
        AppPaths? paths = null)
    {
        _profile = profile;
        _dialogService = dialogService;
        _onSave = onSave;
        _createExecutableSelection = createExecutableSelection;
        _detectAutomaticExecutableSelection = detectAutomaticExecutableSelection;
        _isNameTaken = isNameTaken;
        _isUsvfsAvailable = usvfsAvailable ?? UsvfsFeatureGate.IsEnabled();
        _profileName = profile.Name.Trim();
        _profileDescription = profile.Description;
        _executableRelativePath = profile.ExecutableRelativePath;
        _executableSourcePath = profile.ExecutableSourcePath;
        _launchArguments = profile.LaunchArguments;
        _workspacePath = profile.WorkspacePath;
        _mo2OverwritePath = profile.Mo2OverwritePath;
        _defaultModInstallPath = paths?.GetDefaultModInstallPath(profile.GameInstallPath)
            ?? profile.ModInstallPath;
        _modInstallPath = string.IsNullOrWhiteSpace(profile.ModInstallPath)
            ? _defaultModInstallPath
            : profile.ModInstallPath;
        _isEnabled = profile.IsEnabled;
        _isDiscordStatusEnabled = profile.IsDiscordStatusEnabled;
        _isStandalone = profile.IsStandalone;
        _useBaseGameData = profile.UseBaseGameData;
        _launchBackendKind = profile.LaunchBackendKind;
        _usvfsExecutableOverrideRelativePath = profile.UsvfsExecutableOverrideRelativePath;
        _isAnomalyProfile = IsAnomalyProfile(profile);
        if (AnomalyUsvfsEngineSelection.TryParseRelativePath(
                _usvfsExecutableOverrideRelativePath,
                out var renderer,
                out var useAvx))
        {
            _anomalyRenderer = renderer;
            _anomalyUseAvx = useAvx;
        }

        SaveCommand = new AsyncRelayCommand(async () => await TrySaveAsync());
        BrowseExecutableCommand = new RelayCommand(BrowseExecutable);
        ClearExecutableSourceCommand = new RelayCommand(ClearExecutableSource, () => !string.IsNullOrWhiteSpace(ExecutableSourcePath));
        OpenProfileFolderCommand = new RelayCommand(OpenProfileFolder);
        RemoveMo2OverwriteCommand = new RelayCommand(RemoveMo2Overwrite);
        BrowseModInstallPathCommand = new RelayCommand(BrowseModInstallPath);
        ResetModInstallPathCommand = new RelayCommand(ResetModInstallPath);
        ImportMo2ModListCommand = new AsyncRelayCommand(ImportMo2ModListAsync);
        CopyGameDataCommand = new AsyncRelayCommand(CopyGameDataAsync);
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string ProfileDescription
    {
        get => _profileDescription;
        set => SetProperty(ref _profileDescription, value);
    }

    public string ExecutableRelativePath
    {
        get => _executableRelativePath;
        set
        {
            if (SetProperty(ref _executableRelativePath, value))
            {
                ExecutableSourcePath = string.Empty;
            }
        }
    }

    public string ExecutableSourcePath
    {
        get => _executableSourcePath;
        private set
        {
            if (SetProperty(ref _executableSourcePath, value))
            {
                OnPropertyChanged(nameof(ExecutableSourceDisplay));
                OnPropertyChanged(nameof(HasManualExecutableSource));
                ((RelayCommand)ClearExecutableSourceCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasManualExecutableSource => !string.IsNullOrWhiteSpace(ExecutableSourcePath);

    public string ExecutableSourceDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExecutableSourcePath))
            {
                return "Источник EXE: автоматически по порядку модов. Нижний мод в списке имеет больший приоритет.";
            }

            var source = ProfileExecutableSourceResolver.GetSourceRoots(_profile, includeWorkspace: false)
                .FirstOrDefault(root => FileSystemSafety.IsSameDirectory(root.RootPath, ExecutableSourcePath));
            return source is null
                ? $"Источник EXE: выбран вручную, но папка сейчас недоступна: {ExecutableSourcePath}"
                : $"Источник EXE: вручную закреплен за модом: {source.DisplayName}.";
        }
    }

    public string LaunchArguments
    {
        get => _launchArguments;
        set => SetProperty(ref _launchArguments, value);
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        set => SetProperty(ref _workspacePath, value);
    }

    public string Mo2OverwritePath
    {
        get => _mo2OverwritePath;
        private set
        {
            if (SetProperty(ref _mo2OverwritePath, value))
            {
                OnPropertyChanged(nameof(HasMo2Overwrite));
            }
        }
    }

    public bool HasMo2Overwrite => !string.IsNullOrWhiteSpace(Mo2OverwritePath);

    public string ModInstallPath
    {
        get => _modInstallPath;
        set => SetProperty(ref _modInstallPath, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsDiscordStatusEnabled
    {
        get => _isDiscordStatusEnabled;
        set => SetProperty(ref _isDiscordStatusEnabled, value);
    }

    public bool IsStandalone
    {
        get => _isStandalone;
        set
        {
            if (!SetProperty(ref _isStandalone, value))
            {
                return;
            }

            if (value)
            {
                SetLaunchBackend(LaunchBackendKind.LinkedWorkspace);
            }

            OnPropertyChanged(nameof(CanUseUsvfs));
            OnPropertyChanged(nameof(CanChooseGameData));
            OnPropertyChanged(nameof(GameDataDescription));
            OnPropertyChanged(nameof(IsAnomalyUsvfsOptionsVisible));
        }
    }

    public bool IsUsvfsAvailable => _isUsvfsAvailable;

    public bool CanChooseGameData => !IsStandalone && !_profile.IsRunning;
    public bool UseBaseGameData
    {
        get => _useBaseGameData;
        set
        {
            if (SetProperty(ref _useBaseGameData, value))
            {
                OnPropertyChanged(nameof(UseProfileGameData));
                OnPropertyChanged(nameof(GameDataDescription));
            }
        }
    }
    public bool UseProfileGameData
    {
        get => !UseBaseGameData;
        set { if (value) UseBaseGameData = false; }
    }
    public string GameDataDescription
    {
        get
        {
            if (IsStandalone) return "Автономная сборка использует свои данные без перенаправления.";
            try
            {
                var path = GetSelectedGameDataRoot();
                return $"{(UseBaseGameData ? "Общие сохранения, настройки, логи и скриншоты" : "Отдельные данные профиля")}: {path}\nПереключение не переносит и не заменяет существующие файлы.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                return ex.Message;
            }
        }
    }

    private string GetSelectedGameDataRoot() => ProfileDataPathResolver.GetGameDataRoot(new ModProfile
    {
        GameInstallPath = _profile.GameInstallPath,
        WorkspacePath = WorkspacePath,
        UseBaseGameData = UseBaseGameData,
        Mods = _profile.Mods
    });

    public bool CanUseUsvfs => IsUsvfsAvailable && !IsStandalone;

    public string UsvfsAvailabilityText => IsUsvfsAvailable
        ? "USVFS подключает моды без сборки полного workspace. Совместимость зависит от конкретной игры и модификаций."
        : "Компоненты USVFS не найдены рядом с лаунчером. Доступен только Workspace.";

    public bool UseLinkedWorkspace
    {
        get => _launchBackendKind == LaunchBackendKind.LinkedWorkspace;
        set
        {
            if (value)
            {
                SetLaunchBackend(LaunchBackendKind.LinkedWorkspace);
            }
        }
    }

    public bool UseVirtualFileSystem
    {
        get => _launchBackendKind == LaunchBackendKind.VirtualFileSystem;
        set
        {
            if (value && CanUseUsvfs)
            {
                SetLaunchBackend(LaunchBackendKind.VirtualFileSystem);
            }
        }
    }

    public bool IsAnomalyUsvfsOptionsVisible =>
        _isAnomalyProfile && UseVirtualFileSystem;

    public bool UseAutomaticAnomalyRenderer
    {
        get => _anomalyRenderer.Length == 0;
        set
        {
            if (value)
            {
                SetAnomalyRenderer(string.Empty);
            }
        }
    }

    public bool UseAnomalyDx8
    {
        get => _anomalyRenderer == "DX8";
        set { if (value) SetAnomalyRenderer("DX8"); }
    }

    public bool UseAnomalyDx9
    {
        get => _anomalyRenderer == "DX9";
        set { if (value) SetAnomalyRenderer("DX9"); }
    }

    public bool UseAnomalyDx10
    {
        get => _anomalyRenderer == "DX10";
        set { if (value) SetAnomalyRenderer("DX10"); }
    }

    public bool UseAnomalyDx11
    {
        get => _anomalyRenderer == "DX11";
        set { if (value) SetAnomalyRenderer("DX11"); }
    }

    public bool HasManualAnomalyRenderer => _anomalyRenderer.Length > 0;

    public bool AnomalyUseAvx
    {
        get => _anomalyUseAvx;
        set
        {
            if (SetProperty(ref _anomalyUseAvx, value))
            {
                UpdateAnomalyExecutableOverride();
            }
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand BrowseExecutableCommand { get; }
    public ICommand ClearExecutableSourceCommand { get; }
    public ICommand OpenProfileFolderCommand { get; }
    public ICommand RemoveMo2OverwriteCommand { get; }
    public ICommand BrowseModInstallPathCommand { get; }
    public ICommand ResetModInstallPathCommand { get; }
    public ICommand ImportMo2ModListCommand { get; }
    public ICommand CopyGameDataCommand { get; }

    private async Task CopyGameDataAsync()
    {
        if (!CanChooseGameData) return;
        try
        {
            var profileData = Path.Combine(WorkspacePath, "userdata");
            if (string.IsNullOrWhiteSpace(WorkspacePath)) throw new InvalidOperationException("Сначала создайте рабочую папку профиля запуском игры.");
            var sharedData = ProfileDataPathResolver.GetGameDataRoot(new ModProfile
            {
                GameInstallPath = _profile.GameInstallPath,
                WorkspacePath = WorkspacePath,
                UseBaseGameData = true,
                Mods = _profile.Mods
            });
            var source = UseBaseGameData ? profileData : sharedData;
            var destination = UseBaseGameData ? sharedData : profileData;
            if (!DialogService.Confirm("Копирование пользовательских данных",
                $"Откуда: {source}\nКуда: {destination}\n\nДубликаты файлов будут пропущены. Служебные файлы профиля не копируются. Закройте другие экземпляры игры перед копированием.")) return;
            var result = await Task.Run(() => GameDataCopyService.CopyMissing(source, destination));
            DialogService.ShowInfo("Копирование завершено", $"Скопировано: {result.Copied}. Пропущено: {result.Skipped}.\nДля смены каталога запуска сохраните настройки профиля.");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Не удалось скопировать все данные", $"{ex.Message}\nУже скопированные файлы сохранены. Существующие файлы не заменены.");
        }
    }

    public async Task<bool> TrySaveAsync()
    {
        var validation = ProfileSettingsValidator.Validate(ProfileName, ExecutableRelativePath, _isNameTaken);
        if (!validation.IsValid)
        {
            _dialogService.ShowError("Некорректные настройки профиля", string.Join(Environment.NewLine, validation.Messages));
            return false;
        }

        var snapshot = ProfileSettingsSnapshot.Capture(_profile);
        try
        {
            if (_profile.IsRunning && UseBaseGameData != _profile.UseBaseGameData)
                throw new InvalidOperationException("Завершите игру перед изменением каталога данных.");
            if (!IsStandalone && UseBaseGameData) _ = GetSelectedGameDataRoot();
            ApplyToProfile();
            await _onSave();
            return true;
        }
        catch (Exception ex)
        {
            snapshot.Restore(_profile);
            _dialogService.ShowError(
                "Не удалось сохранить настройки профиля",
                $"Изменения не сохранены. Окно останется открытым.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }
    }

    private void ApplyToProfile()
    {
        _profile.Name = ProfileName.Trim();
        _profile.Description = ProfileDescription;
        _profile.ExecutableRelativePath = ExecutableRelativePath;
        _profile.ExecutableSourcePath = IsStandalone ? string.Empty : ExecutableSourcePath;
        _profile.LaunchArguments = LaunchArguments;
        _profile.WorkspacePath = WorkspacePath;
        _profile.Mo2OverwritePath = Mo2OverwritePath;
        _profile.ModInstallPath = string.IsNullOrWhiteSpace(ModInstallPath)
            ? string.Empty
            : Path.GetFullPath(ModInstallPath.Trim());
        _profile.IsEnabled = IsEnabled;
        _profile.IsDiscordStatusEnabled = IsDiscordStatusEnabled;
        _profile.IsStandalone = IsStandalone;
        _profile.UseBaseGameData = UseBaseGameData;
        _profile.LaunchBackendKind = IsStandalone
            ? LaunchBackendKind.LinkedWorkspace
            : _launchBackendKind;
        _profile.UsvfsExecutableOverrideRelativePath = _usvfsExecutableOverrideRelativePath;
    }

    private void BrowseExecutable()
    {
        if (_isStandalone)
        {
            BrowseStandaloneExecutable();
            return;
        }

        var initialPath = Directory.Exists(_workspacePath) ? _workspacePath : null;
        var selected = DialogService.PickExecutable("Choose launch executable", initialPath);
        if (selected is null)
        {
            return;
        }

        var selection = _createExecutableSelection(selected);
        if (selection is null)
        {
            _dialogService.ShowError(
                "Invalid executable",
                "Choose an executable from the game folder, an enabled mod folder, or the generated profile workspace.");
            return;
        }

        SetExecutableSelection(selection);
    }

    private void BrowseStandaloneExecutable()
    {
        var modRoot = _profile.Mods
            .FirstOrDefault(m => m.IsEnabled && Directory.Exists(m.SourcePath))
            ?.SourcePath;

        if (modRoot is null)
        {
            _dialogService.ShowError(
                "No mod folder",
                "Add a mod to the profile first, then choose the executable from its folder.");
            return;
        }

        var selected = DialogService.PickExecutable("Choose game executable", modRoot);
        if (selected is null)
        {
            return;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(modRoot), selected);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            _dialogService.ShowError(
                "Outside mod folder",
                "The executable must be inside the standalone mod's folder.");
            return;
        }

        SetExecutableSelection(new ProfileExecutableSelection(relative, modRoot, "автономная сборка", true));
    }

    private void SetExecutableSelection(ProfileExecutableSelection selection)
    {
        _executableRelativePath = selection.RelativePath;
        OnPropertyChanged(nameof(ExecutableRelativePath));
        ExecutableSourcePath = !IsStandalone && selection.PinsSource ? selection.SourceRootPath : string.Empty;
    }

    private void ClearExecutableSource()
    {
        var selection = _detectAutomaticExecutableSelection();
        if (selection is null)
        {
            _dialogService.ShowError(
                "Не удалось выбрать EXE автоматически",
                "Лаунчер не нашел подходящий .exe в папке базовой игры или во включенных модах. Выберите файл запуска вручную.");
            return;
        }

        SetExecutableSelection(selection);
    }

    private void OpenProfileFolder()
    {
        try
        {
            var path = _isStandalone
                ? _profile.Mods.FirstOrDefault(m => m.IsEnabled && Directory.Exists(m.SourcePath))?.SourcePath
                : null;

            path ??= string.IsNullOrWhiteSpace(_workspacePath) ? null : _workspacePath;

            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(path);
            DialogService.OpenFolder(path);
        }
        catch
        {
            // ignored
        }
    }

    private void RemoveMo2Overwrite() => Mo2OverwritePath = string.Empty;

    private void BrowseModInstallPath()
    {
        var selected = DialogService.PickFolder(
            "Выберите папку для распакованных модов",
            Directory.Exists(ModInstallPath) ? ModInstallPath : null);
        if (selected is not null)
        {
            ModInstallPath = selected;
        }
    }

    private void ResetModInstallPath()
    {
        if (!string.IsNullOrWhiteSpace(_defaultModInstallPath))
        {
            ModInstallPath = _defaultModInstallPath;
        }
    }

    private async Task ImportMo2ModListAsync()
    {
        var initialPath = _profile.Mods
            .Select(mod => Path.GetDirectoryName(mod.SourcePath))
            .FirstOrDefault(Directory.Exists);
        var filePath = DialogService.PickFile(
            "Выберите modlist.txt из профиля Mod Organizer 2",
            "Mod Organizer mod list (modlist.txt)|modlist.txt|Text files (*.txt)|*.txt",
            initialPath);
        if (filePath is null)
        {
            return;
        }

        try
        {
            var result = Mo2ModListImporter.Import(_profile, filePath);
            await _onSave();

            var report = new List<string>
            {
                "Файл modlist.txt применён к уже добавленным модам.",
                string.Empty,
                $"Сопоставлено модов: {result.MatchedCount}",
                $"Изменено состояний включения: {result.EnabledStateChanges}",
                $"Не найдены среди добавленных модов: {result.MissingProfileMods.Count}",
                $"Отсутствуют в modlist.txt: {result.UnlistedLauncherMods.Count}"
            };

            if (result.MissingProfileMods.Count > 0)
            {
                report.Add(string.Empty);
                report.Add("Первые несопоставленные записи:");
                report.AddRange(result.MissingProfileMods.Take(8).Select(name => $"• {name}"));
            }

            DialogService.ShowInfo("Применить только modlist.txt", string.Join(Environment.NewLine, report));
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Не удалось применить modlist.txt", ex.Message);
        }
    }

    private void SetLaunchBackend(LaunchBackendKind backend)
    {
        if (_launchBackendKind == backend)
        {
            return;
        }

        _launchBackendKind = backend;
        OnPropertyChanged(nameof(UseLinkedWorkspace));
        OnPropertyChanged(nameof(UseVirtualFileSystem));
        OnPropertyChanged(nameof(IsAnomalyUsvfsOptionsVisible));
    }

    private void SetAnomalyRenderer(string renderer)
    {
        if (_anomalyRenderer == renderer)
        {
            return;
        }

        _anomalyRenderer = renderer;
        OnPropertyChanged(nameof(UseAutomaticAnomalyRenderer));
        OnPropertyChanged(nameof(UseAnomalyDx8));
        OnPropertyChanged(nameof(UseAnomalyDx9));
        OnPropertyChanged(nameof(UseAnomalyDx10));
        OnPropertyChanged(nameof(UseAnomalyDx11));
        OnPropertyChanged(nameof(HasManualAnomalyRenderer));
        UpdateAnomalyExecutableOverride();
    }

    private void UpdateAnomalyExecutableOverride()
    {
        _usvfsExecutableOverrideRelativePath = _anomalyRenderer.Length == 0
            ? string.Empty
            : AnomalyUsvfsEngineSelection.CreateRelativePath(_anomalyRenderer, _anomalyUseAvx);
    }

    private static bool IsAnomalyProfile(ModProfile profile)
    {
        return AnomalyLauncherLocator.HasConfiguration(profile.GameInstallPath) ||
               Path.GetFileName(profile.ExecutableRelativePath)
                   .StartsWith("Anomaly", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProfileSettingsSnapshot(
        string Name,
        string Description,
        string ExecutableRelativePath,
        string ExecutableSourcePath,
        string LaunchArguments,
        string WorkspacePath,
        string Mo2OverwritePath,
        string ModInstallPath,
        bool IsEnabled,
        bool IsDiscordStatusEnabled,
        bool IsStandalone,
        bool UseBaseGameData,
        LaunchBackendKind LaunchBackendKind,
        string UsvfsExecutableOverrideRelativePath)
    {
        public static ProfileSettingsSnapshot Capture(ModProfile profile) => new(
            profile.Name,
            profile.Description,
            profile.ExecutableRelativePath,
            profile.ExecutableSourcePath,
            profile.LaunchArguments,
            profile.WorkspacePath,
            profile.Mo2OverwritePath,
            profile.ModInstallPath,
            profile.IsEnabled,
            profile.IsDiscordStatusEnabled,
            profile.IsStandalone,
            profile.UseBaseGameData,
            profile.LaunchBackendKind,
            profile.UsvfsExecutableOverrideRelativePath);

        public void Restore(ModProfile profile)
        {
            profile.Name = Name;
            profile.Description = Description;
            profile.ExecutableRelativePath = ExecutableRelativePath;
            profile.ExecutableSourcePath = ExecutableSourcePath;
            profile.LaunchArguments = LaunchArguments;
            profile.WorkspacePath = WorkspacePath;
            profile.Mo2OverwritePath = Mo2OverwritePath;
            profile.ModInstallPath = ModInstallPath;
            profile.IsEnabled = IsEnabled;
            profile.IsDiscordStatusEnabled = IsDiscordStatusEnabled;
            profile.IsStandalone = IsStandalone;
            profile.UseBaseGameData = UseBaseGameData;
            profile.LaunchBackendKind = LaunchBackendKind;
            profile.UsvfsExecutableOverrideRelativePath = UsvfsExecutableOverrideRelativePath;
        }
    }
}
