using System.Collections.ObjectModel;
using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ProfileCreationViewModel : ObservableObject
{
    private int _step = 1;
    private string _name = "Новый профиль";
    private bool _isStandalone;
    private string _gamePath = string.Empty;
    private string _executableRelativePath = @"bin\xr_3da.exe";
    private string _executableSourcePath = string.Empty;
    private string _launchArguments = "-nointro";
    private string _message = "Выберите тип профиля и задайте понятное название.";
    private string _executableDetectionMessage = string.Empty;
    private bool _isMessageWarning;

    public ProfileCreationViewModel()
    {
        NextCommand = new RelayCommand(Next, () => Step < 3);
        BackCommand = new RelayCommand(Back, () => Step > 1);
        FinishCommand = new RelayCommand(Finish, () => Step == 3);
        BrowseGameCommand = new RelayCommand(BrowseGame);
        BrowseStandaloneCommand = new RelayCommand(BrowseStandalone);
        AddModCommand = new RelayCommand(AddMod, () => !IsStandalone || Mods.Count == 0);
        RemoveModCommand = new RelayCommand(parameter => RemoveMod(parameter as ModEntry), parameter => parameter is ModEntry);
        BrowseExecutableCommand = new RelayCommand(BrowseExecutable);
        Mods.CollectionChanged += (_, _) => OnModListChanged();
    }

    public event EventHandler<ModProfile>? Completed;

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
        1 => "Тип профиля",
        2 => IsStandalone ? "Автономная сборка" : "Игра и моды",
        _ => "Запуск профиля"
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
        ? "Выберите корневую директорию с модом или игрой."
        : "Добавьте папки модов. Моды ниже в списке имеют больший приоритет.";

    public string ProfileTypeDescription => IsStandalone
        ? "Запуск автономной сборки как есть, без добавления модов."
        : "Выберите игру или готовую сборку, добавляйте и отключайте моды — исходные папки не изменятся.";

    public string ProfileTypeLabel => IsStandalone
        ? "Автономная сборка"
        : "Игра с модами";

    public string SourceStepDescription => IsStandalone
        ? "Выберите корневую директорию с модом или игрой."
        : "Выберите папку базовой игры и добавьте моды в нужном порядке.";

    public string SourceSummary
    {
        get
        {
            if (IsStandalone)
            {
                return string.IsNullOrWhiteSpace(StandalonePath)
                    ? "Автономная папка ещё не выбрана."
                    : $"Автономная папка: {StandalonePath}";
            }

            var game = string.IsNullOrWhiteSpace(GamePath) ? "не выбрана" : GamePath;
            return $"Базовая игра: {game}. Модов в профиле: {Mods.Count}.";
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
        set => SetProperty(ref _launchArguments, value);
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
            : "Проверьте, какой EXE будет запускаться. При необходимости выберите другой файл вручную.");

        if (Step == 3)
        {
            AutoDetectExecutable();
        }
    }

    private void Back()
    {
        Step--;
        SetMessage(Step == 1
            ? "Выберите тип профиля и задайте понятное название."
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
            Name = Name.Trim(),
            Description = IsStandalone ? "Автономная сборка" : "Мод поверх базовой игры",
            IsStandalone = IsStandalone,
            LaunchBackendKind = IsStandalone
                ? LaunchBackendKind.LinkedWorkspace
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
            SetMessage("Введите название профиля.", isWarning: true);
            return false;
        }

        if (Step == 2)
        {
            if (!IsStandalone && !Directory.Exists(GamePath))
            {
                SetMessage("Выберите существующую папку базовой игры.", isWarning: true);
                return false;
            }

            if (IsStandalone && (Mods.Count != 1 || !Directory.Exists(Mods[0].SourcePath)))
            {
                SetMessage("Для автономного профиля выберите одну папку мода.", isWarning: true);
                return false;
            }
        }

        if (Step == 3)
        {
            try
            {
                FileSystemSafety.EnsureRelativePath(ExecutableRelativePath, "Бинарник запуска");
                if (FindExactExecutableSource(CreateExecutableSearchRoots().ToArray(), ExecutableRelativePath) is null)
                {
                    SetMessage("Файл запуска не найден в выбранных папках. Выберите EXE вручную или вернитесь к источникам файлов.", isWarning: true);
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, isWarning: true);
                return false;
            }
        }

        return true;
    }

    private void BrowseGame()
    {
        var selected = DialogService.PickFolder("Выберите папку базовой игры", GamePath);
        if (selected is not null)
        {
            GamePath = selected;
            ExecutableDetectionMessage = string.Empty;
        }
    }

    private void BrowseStandalone()
    {
        var selected = DialogService.PickFolder("Выберите папку автономной игры или мода", StandalonePath);
        if (selected is null)
        {
            return;
        }

        Mods.Clear();
        AddModPath(selected);
    }

    private void AddMod()
    {
        var selected = DialogService.PickFolder(IsStandalone ? "Выберите папку автономной сборки" : "Выберите папку мода");
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

        var selected = DialogService.PickExecutable("Выберите запускаемый файл", initial);
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

        SetMessage("Выбранный бинарник должен находиться внутри папки игры или одного из модов.", isWarning: true);
    }

    private void AutoDetectExecutable()
    {
        var roots = CreateExecutableSearchRoots().ToArray();
        var currentExact = FindExactExecutableSource(roots, ExecutableRelativePath);
        if (!ExecutableRelativePath.Equals(@"bin\xr_3da.exe", StringComparison.OrdinalIgnoreCase) &&
            currentExact is not null)
        {
            ExecutableDetectionMessage =
                $"Используется выбранный путь: {ExecutableRelativePath}. Источник: {currentExact.Value.SourceName}.";
            return;
        }

        var detected = LaunchExecutableDetector.DetectBest(roots, requestedRelativePath: null);
        if (detected is null)
        {
            ExecutableDetectionMessage = "EXE не найден автоматически. Выберите запускаемый файл вручную.";
            return;
        }

        ExecutableRelativePath = detected.RelativePath;
        _executableSourcePath = string.Empty;
        ExecutableDetectionMessage = $"Автоматически выбран: {detected.Summary}";
    }

    private void SetExecutableSelection(string relativePath, string sourceRootPath, string sourceName)
    {
        _executableRelativePath = relativePath;
        _executableSourcePath = IsStandalone ? string.Empty : Path.GetFullPath(sourceRootPath);
        OnPropertyChanged(nameof(ExecutableRelativePath));
        ExecutableDetectionMessage = IsStandalone
            ? $"Выбран вручную: {relativePath}."
            : $"Выбран вручную: {relativePath}. Источник: {sourceName}.";
    }

    private IEnumerable<LaunchExecutableSearchRoot> CreateExecutableSearchRoots()
    {
        if (!IsStandalone && Directory.Exists(GamePath))
        {
            yield return new LaunchExecutableSearchRoot(GamePath, "базовая игра", 0, IsBaseGameRoot: true);
        }

        foreach (var mod in Mods.OrderBy(mod => mod.Order).Where(mod => Directory.Exists(mod.SourcePath)))
        {
            yield return new LaunchExecutableSearchRoot(
                mod.SourcePath,
                $"мод: {mod.Name}",
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
            FileSystemSafety.EnsureRelativePath(relativePath, "Бинарник запуска");
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
