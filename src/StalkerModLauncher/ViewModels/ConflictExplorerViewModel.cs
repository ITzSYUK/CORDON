using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ConflictExplorerViewModel : ObservableObject, IDisposable
{
    private readonly ModProfile _profile;
    private readonly ModConflictAnalyzer _conflictAnalyzer;
    private readonly DialogService _dialogService;
    private readonly Func<Task> _persistAsync;
    private readonly Action _changed;
    private CancellationTokenSource? _refreshCancellation;
    private ModEntry? _selectedMod;
    private string _searchText = string.Empty;
    private FinalFileFilter _finalFileFilter;
    private string _summary = "Анализ файлов...";
    private bool _isBusy;
    private int _selectedTabIndex;
    private IReadOnlyList<ConflictFileEntry> _winningFiles = [];
    private IReadOnlyList<ConflictFileEntry> _losingFiles = [];
    private IReadOnlyList<ConflictFileEntry> _uniqueFiles = [];
    private IReadOnlyList<FinalFileEntry> _finalFiles = [];
    private IReadOnlyList<FinalFileEntry> _visibleFinalFiles = [];

    public ConflictExplorerViewModel(
        ModProfile profile,
        ModEntry? selectedMod,
        ModConflictAnalyzer conflictAnalyzer,
        DialogService dialogService,
        Func<Task> persistAsync,
        Action changed)
    {
        _profile = profile;
        _selectedMod = selectedMod ?? profile.Mods.FirstOrDefault();
        _selectedTabIndex = _selectedMod is null ? 1 : 0;
        _conflictAnalyzer = conflictAnalyzer;
        _dialogService = dialogService;
        _persistAsync = persistAsync;
        _changed = changed;

        ToggleFileCommand = new RelayCommand(
            parameter => _ = ToggleFileAsync(parameter as ConflictFileEntry),
            parameter => parameter is ConflictFileEntry { CanExclude: true } && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        _ = RefreshAsync();
    }

    public string ProfileName => _profile.Name;
    public IReadOnlyList<ModEntry> Mods => _profile.Mods.OrderBy(mod => mod.Order).ToArray();
    public IReadOnlyList<FinalFileFilterOption> FinalFileFilters { get; } =
    [
        new(FinalFileFilter.All, "Все файлы"),
        new(FinalFileFilter.Conflicts, "Только конфликты"),
        new(FinalFileFilter.Binaries, "EXE и DLL"),
        new(FinalFileFilter.Configuration, "Настройки и скрипты")
    ];

    public IReadOnlyList<ConflictFileEntry> WinningFiles
    {
        get => _winningFiles;
        private set => SetProperty(ref _winningFiles, value);
    }

    public IReadOnlyList<ConflictFileEntry> LosingFiles
    {
        get => _losingFiles;
        private set => SetProperty(ref _losingFiles, value);
    }

    public IReadOnlyList<ConflictFileEntry> UniqueFiles
    {
        get => _uniqueFiles;
        private set => SetProperty(ref _uniqueFiles, value);
    }

    public IReadOnlyList<FinalFileEntry> FinalFiles
    {
        get => _finalFiles;
        private set => SetProperty(ref _finalFiles, value);
    }

    public IReadOnlyList<FinalFileEntry> VisibleFinalFiles
    {
        get => _visibleFinalFiles;
        private set => SetProperty(ref _visibleFinalFiles, value);
    }

    public ModEntry? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                _ = RefreshAsync();
                OnPropertyChanged(nameof(HasSelectedMod));
            }
        }
    }

    public bool HasSelectedMod => SelectedMod is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFinalFileFilter();
            }
        }
    }

    public FinalFileFilter SelectedFinalFileFilter
    {
        get => _finalFileFilter;
        set
        {
            if (SetProperty(ref _finalFileFilter, value))
            {
                ApplyFinalFileFilter();
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                ToggleFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public RelayCommand ToggleFileCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public async Task RefreshAsync()
    {
        if (_refreshCancellation is not null)
        {
            await _refreshCancellation.CancelAsync();
        }

        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        try
        {
            IsBusy = true;
            Summary = "Анализ файлов...";
            var workspace = string.IsNullOrWhiteSpace(_profile.WorkspacePath)
                ? Path.Combine(AppPaths.Current.TempDirectory, "analysis", _profile.Id)
                : _profile.WorkspacePath;
            var plan = FileLayerPlan.CreateLinkedWorkspace(_profile.GameInstallPath, _profile, workspace);
            var finalTreeTask = FileLayerExplorerService.BuildFinalTreeAsync(plan, workspace, cancellationToken);
            var conflictTask = AnalyzeSelectedModAsync(cancellationToken);
            await Task.WhenAll(finalTreeTask, conflictTask);

            cancellationToken.ThrowIfCancellationRequested();
            FinalFiles = await finalTreeTask;

            PopulateModFiles(await conflictTask);
            ApplyFinalFileFilter();
            Summary = $"Итоговых файлов: {FinalFiles.Count:N0}; конфликтов путей: {FinalFiles.Count(file => file.HasConflict):N0}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Summary = $"Анализ не выполнен: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SelectedModAnalysis?> AnalyzeSelectedModAsync(CancellationToken cancellationToken)
    {
        if (SelectedMod is null || !Directory.Exists(SelectedMod.SourcePath))
        {
            return null;
        }

        var selected = SelectedMod;
        var inputs = _profile.Mods
            .OrderBy(mod => mod.Order)
            .Select(mod => new ModConflictInput(
                mod.Id,
                mod.Name,
                mod.SourcePath,
                mod.IsEnabled || ReferenceEquals(mod, selected),
                ReferenceEquals(mod, selected) ? [] : mod.ExcludedFiles))
            .ToArray();
        var states = await _conflictAnalyzer.AnalyzeAsync(inputs, cancellationToken);
        var state = states.GetValueOrDefault(selected.Id);
        var files = await Task.Run(
            () => Directory.EnumerateFiles(selected.SourcePath, "*", SafeEnumerationOptions)
                .Select(path => Path.GetRelativePath(selected.SourcePath, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cancellationToken);
        return new SelectedModAnalysis(selected, state, files);
    }

    private void PopulateModFiles(SelectedModAnalysis? analysis)
    {
        var winning = new List<ConflictFileEntry>();
        var losing = new List<ConflictFileEntry>();
        var unique = new List<ConflictFileEntry>();
        if (analysis is null)
        {
            WinningFiles = winning;
            LosingFiles = losing;
            UniqueFiles = unique;
            return;
        }

        var conflicts = analysis.State?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ModConflictFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in analysis.Files)
        {
            var isExcluded = analysis.Mod.ExcludedFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
            if (!conflicts.TryGetValue(relativePath, out var conflict))
            {
                unique.Add(new ConflictFileEntry(relativePath, "Уникальный файл", "—", false, isExcluded));
                continue;
            }

            var loses = conflict.HigherPriorityModNames.Count > 0;
            var otherMods = loses
                ? conflict.HigherPriorityModNames
                : conflict.LowerPriorityModNames;
            var item = new ConflictFileEntry(
                relativePath,
                loses ? "Проигрывает" : "Побеждает",
                string.Join(", ", otherMods),
                true,
                isExcluded);
            (loses ? losing : winning).Add(item);
        }

        WinningFiles = winning;
        LosingFiles = losing;
        UniqueFiles = unique;
    }

    private async Task ToggleFileAsync(ConflictFileEntry? file)
    {
        if (file is null || SelectedMod is null || !file.CanExclude)
        {
            return;
        }

        var mod = SelectedMod;
        var wasExcluded = mod.ExcludedFiles.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (wasExcluded)
        {
            mod.ExcludedFiles.RemoveAll(path => path.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            mod.ExcludedFiles.Add(file.RelativePath);
        }

        try
        {
            await _persistAsync();
            _changed();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            if (wasExcluded)
            {
                mod.ExcludedFiles.Add(file.RelativePath);
            }
            else
            {
                mod.ExcludedFiles.RemoveAll(path => path.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
            }

            _dialogService.ShowError("Не удалось изменить файл мода", ex.Message);
        }
    }

    private void ApplyFinalFileFilter()
    {
        var search = SearchText.Trim();
        var files = FinalFiles.Where(file =>
            (search.Length == 0 ||
             file.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             file.FinalProvider.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            SelectedFinalFileFilter switch
            {
                FinalFileFilter.Conflicts => file.HasConflict,
                FinalFileFilter.Binaries => file.IsBinary,
                FinalFileFilter.Configuration => file.IsConfiguration,
                _ => true
            });

        VisibleFinalFiles = files.ToArray();
    }

    public void Dispose()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
    }

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private sealed record SelectedModAnalysis(ModEntry Mod, ModConflictState? State, IReadOnlyList<string> Files);
}

public sealed record ConflictFileEntry(
    string RelativePath,
    string Status,
    string OtherMods,
    bool CanExclude,
    bool IsExcluded)
{
    public string ActionText => IsExcluded ? "Вернуть" : "Исключить";
}

public enum FinalFileFilter
{
    All,
    Conflicts,
    Binaries,
    Configuration
}

public sealed record FinalFileFilterOption(FinalFileFilter Value, string Name);
