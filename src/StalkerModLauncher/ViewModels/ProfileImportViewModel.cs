using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ProfileImportViewModel : ObservableObject, IDisposable
{
    private readonly ModProfile _profile;
    private readonly Func<ModProfile, Task<bool>> _commit;
    private string _profileName;
    private string _gamePath;
    private string _searchText = string.Empty;
    private bool _showOnlyProblems;
    private readonly ExportedProfile _sourceProfile;
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;
    private string _executableSummary = string.Empty;
    private string _error = string.Empty;

    public ProfileImportViewModel(ModProfile profile, Func<ModProfile, Task<bool>> commit)
    {
        _profile = profile;
        _commit = commit;
        _profileName = profile.Name;
        _gamePath = profile.GameInstallPath;
        _sourceProfile = ProfileTransferService.ToExportedProfile(profile);
        Entries = profile.Mods.OrderBy(mod => mod.Order).Select(mod => new Mo2ImportPreviewEntry(
            mod.Order, mod.Name, mod.SourcePath, mod.IsEnabled, mod.GroupName, false, [mod.SourcePath])).ToArray();
        foreach (var entry in Entries)
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Mo2ImportPreviewEntry.SourcePath)) RefreshPreview();
            };
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => ImportBlockedReason.Length == 0);
        BrowseMissingModFolderCommand = new RelayCommand(parameter =>
        {
            if (parameter is not Mo2ImportPreviewEntry entry) return;
            var path = DialogService.PickFolder(Strings.Mo2Service_SelectFolder, entry.SourcePath);
            if (path is not null) entry.SourcePath = path;
        }, parameter => parameter is Mo2ImportPreviewEntry { CanBrowseSource: true });
        BrowseGameCommand = new RelayCommand(() =>
        {
            var path = DialogService.PickFolder(Strings.Dialog_SelectGogFolder, GamePath);
            if (path is not null) GamePath = path;
        });
        RefreshPreview();
    }

    public event EventHandler? Completed;
    internal Task PreviewRefresh { get; private set; } = Task.CompletedTask;
    public AsyncRelayCommand ImportCommand { get; }
    public IReadOnlyList<Mo2ImportPreviewEntry> Entries { get; }
    public RelayCommand BrowseMissingModFolderCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value))
            {
                OnPropertyChanged(nameof(ImportBlockedReason));
                ImportCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (SetProperty(ref _gamePath, value)) RefreshPreview();
        }
    }
    public string GamePathStatus => Directory.Exists(GamePath)
        ? Strings.Validation_GameAvailable
        : string.IsNullOrWhiteSpace(GamePath) ? Strings.Validation_GameNotSelected : Strings.ProfileImport_BaseGameMissing;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) OnPropertyChanged(nameof(VisibleEntries));
        }
    }
    public bool ShowOnlyProblems
    {
        get => _showOnlyProblems;
        set
        {
            if (SetProperty(ref _showOnlyProblems, value)) OnPropertyChanged(nameof(VisibleEntries));
        }
    }
    public IEnumerable<Mo2ImportPreviewEntry> VisibleEntries => Entries.Where(entry =>
        (!ShowOnlyProblems || !entry.IsAvailable) &&
        (entry.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ||
         entry.GroupName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)));
    public string Summary => LocalizedText.Format(Strings.ProfileImport_SummaryFormat,
        Entries.Count(entry => entry.IsAvailable), Entries.Count(entry => entry.IsEnabled), Entries.Count(entry => !entry.IsAvailable));
    public string ExecutableSummary { get => _executableSummary; private set => SetProperty(ref _executableSummary, value); }
    public string ImportBlockedReason => string.IsNullOrWhiteSpace(ProfileName)
        ? Strings.Validation_NameRequired
        : Entries.Any(entry => entry.IsEnabled && !entry.IsAvailable) ? Strings.ProfileImport_MissingEnabledMods : string.Empty;

    private void RefreshPreview()
    {
        if (_disposed) return;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        ExecutableSummary = string.Empty;
        PreviewRefresh = RefreshExecutableSummaryAsync(_previewCancellation.Token);
        OnPropertyChanged(nameof(GamePathStatus));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(ImportBlockedReason));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshExecutableSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var draft = CreateDraftProfile();
            await Task.Delay(200, cancellationToken);
            var summary = await Task.Run(() => ResolveExecutableSummary(draft, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ExecutableSummary = summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested) ExecutableSummary = ex.Message;
        }
    }

    private static string ResolveExecutableSummary(ModProfile profile, CancellationToken cancellationToken)
    {
        LaunchExecutableResolution? executable;
        if (profile.IsStandalone)
        {
            var launch = ProfileLaunchPlanResolver.PreviewStandalone(profile, cancellationToken);
            if (launch.Error is not null) return launch.Error;
            executable = launch.Executable;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(profile.GameInstallPath)) return Strings.Validation_GameNotSelected;
            var roots = FileLayerPlan.CreateSourceLayers(profile.GameInstallPath, profile)
                .Select(layer => new LaunchExecutableSearchRoot(
                    layer.RootPath, FileLayerPlan.GetDisplayName(layer), layer.Order, Mod: layer.Mod)).ToArray();
            executable = ProfileLaunchPlanResolver.ResolveExecutableSource(
                profile, roots, profile.ExecutableRelativePath,
                allowPinnedSource: true,
                allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(profile.ExecutableRelativePath),
                cancellationToken);
        }

        return executable is null
            ? LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, profile.ExecutableRelativePath)
            : !executable.IsAvailable ? executable.Reason
            : LocalizedText.Format(Strings.Executable_SummaryFormat, executable.RelativePath, executable.Reason, executable.SourceName);
    }

    private ModProfile CreateDraftProfile()
    {
        var draft = ProfileTransferService.ToModProfile(_sourceProfile);
        var roots = draft.Mods.Select((mod, index) => (OldPath: mod.SourcePath, NewPath: Entries[index].SourcePath))
            .Prepend((OldPath: draft.GameInstallPath, NewPath: GamePath.Trim()))
            .Where(root => !string.IsNullOrWhiteSpace(root.OldPath) && !string.IsNullOrWhiteSpace(root.NewPath))
            .OrderByDescending(root => root.OldPath.Length).ToArray();
        string RebasePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            foreach (var root in roots)
                if (FileSystemSafety.IsDirectoryInside(path, root.OldPath))
                    return Path.GetFullPath(Path.Combine(root.NewPath, Path.GetRelativePath(root.OldPath, path)));
            return path;
        }

        draft.ExecutableSourcePath = RebasePath(draft.ExecutableSourcePath);
        draft.FsgameSourcePath = RebasePath(draft.FsgameSourcePath);
        draft.Name = ProfileName.Trim();
        draft.GameInstallPath = GamePath.Trim();
        for (var index = 0; index < Entries.Count; index++)
            draft.Mods[index].SourcePath = Entries[index].SourcePath;
        return draft;
    }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    private async Task ImportAsync()
    {
        try
        {
            if (ImportBlockedReason.Length > 0) return;
            Error = string.Empty;
            var draft = CreateDraftProfile();
            foreach (var (mod, imported) in _profile.Mods.OrderBy(mod => mod.Order).Zip(draft.Mods))
                mod.SourcePath = imported.SourcePath;
            _profile.Name = draft.Name;
            _profile.GameInstallPath = draft.GameInstallPath;
            _profile.ExecutableSourcePath = draft.ExecutableSourcePath;
            _profile.FsgameSourcePath = draft.FsgameSourcePath;
            if (await _commit(_profile)) Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        Completed = null;
    }
}
