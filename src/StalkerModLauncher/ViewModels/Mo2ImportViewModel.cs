using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class Mo2ImportViewModel : ObservableObject
{
    private readonly Func<Models.ModProfile, Task<bool>> _commitProfileAsync;
    private Mo2ImportDiscovery? _discovery;
    private Mo2ProfileSource? _selectedMo2Profile;
    private Mo2ImportPreview? _preview;
    private int _step = 1;
    private string _sourcePath = string.Empty;
    private string _gamePath = string.Empty;
    private string _modsPath = string.Empty;
    private string _overwritePath = string.Empty;
    private string _profileName = string.Empty;
    private string _message = Strings.Mo2_SelectSource;
    private bool _isMessageWarning;
    private bool _includeOverwrite;

    public Mo2ImportViewModel(
        Func<Models.ModProfile, Task<bool>> commitProfileAsync)
    {
        _commitProfileAsync = commitProfileAsync;
        BrowseSourceFolderCommand = new RelayCommand(BrowseSourceFolder);
        BrowseModListCommand = new RelayCommand(BrowseModList);
        BrowseGameCommand = new RelayCommand(BrowseGame);
        BrowseModsCommand = new RelayCommand(BrowseMods);
        BrowseOverwriteCommand = new RelayCommand(BrowseOverwrite);
        NextCommand = new RelayCommand(CreatePreview, CanCreatePreview);
        BackCommand = new RelayCommand(Back, () => Step == 2);
        ImportCommand = new AsyncRelayCommand(ImportAsync, CanImport);
    }

    public event EventHandler? Completed;

    public ObservableCollection<Mo2ProfileSource> Profiles { get; } = new();

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsSourceStep));
                OnPropertyChanged(nameof(IsPreviewStep));
                RaiseCommandStates();
            }
        }
    }

    public bool IsSourceStep => Step == 1;
    public bool IsPreviewStep => Step == 2;

    public string SourcePath
    {
        get => _sourcePath;
        private set => SetProperty(ref _sourcePath, value);
    }

    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (SetProperty(ref _gamePath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ModsPath
    {
        get => _modsPath;
        set
        {
            if (SetProperty(ref _modsPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string OverwritePath
    {
        get => _overwritePath;
        set => SetProperty(ref _overwritePath, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public Mo2ProfileSource? SelectedMo2Profile
    {
        get => _selectedMo2Profile;
        set
        {
            if (SetProperty(ref _selectedMo2Profile, value))
            {
                if (value is not null)
                {
                    ProfileName = value.Name;
                }

                ReplacePreview(null);
                OnPropertyChanged(nameof(Preview));
                RaiseCommandStates();
            }
        }
    }

    public Mo2ImportPreview? Preview => _preview;

    public bool IncludeOverwrite
    {
        get => _includeOverwrite;
        set => SetProperty(ref _includeOverwrite, value);
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

    public string PreviewSummary => Preview is null
        ? string.Empty
        : LocalizedText.Format(
            Strings.Mo2_PreviewSummaryFormat,
            Preview.FoundModCount,
            Preview.EnabledModCount,
            Preview.MissingModCount,
            Preview.AmbiguousModCount,
            Preview.SeparatorCount);

    public ICommand BrowseSourceFolderCommand { get; }
    public ICommand BrowseModListCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand BrowseModsCommand { get; }
    public ICommand BrowseOverwriteCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ImportCommand { get; }

    public void LoadSource(string path)
    {
        try
        {
            var discovery = Mo2ImportService.Discover(path);
            _discovery = discovery;
            SourcePath = path;
            GamePath = discovery.GamePath;
            ModsPath = discovery.ModsPath;
            OverwritePath = discovery.OverwritePath;
            Profiles.Clear();
            foreach (var profile in discovery.Profiles)
            {
                Profiles.Add(profile);
            }

            SelectedMo2Profile = discovery.SelectedProfile;
            SetMessage(LocalizedText.Format(Strings.Mo2_ProfilesFoundFormat, Profiles.Count));
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, isWarning: true);
        }
    }

    private void BrowseSourceFolder()
    {
        var path = DialogService.PickFolder(Strings.Mo2_PickSource, SourcePath);
        if (path is not null)
        {
            LoadSource(path);
        }
    }

    private void BrowseModList()
    {
        var path = DialogService.PickFile(
            Strings.Mo2_PickModList,
            Strings.Dialog_ModListFilter);
        if (path is not null)
        {
            LoadSource(path);
        }
    }

    private void BrowseGame()
    {
        var path = DialogService.PickFolder(Strings.Common_ChooseBaseGame, GamePath);
        if (path is not null)
        {
            GamePath = path;
        }
    }

    private void BrowseMods()
    {
        var path = DialogService.PickFolder(Strings.Mo2_PickModsFolder, ModsPath);
        if (path is not null)
        {
            ModsPath = path;
        }
    }

    private void BrowseOverwrite()
    {
        var path = DialogService.PickFolder(Strings.Mo2_PickOverwriteFolder, OverwritePath);
        if (path is not null)
        {
            OverwritePath = path;
        }
    }

    private bool CanCreatePreview() =>
        _discovery is not null &&
        SelectedMo2Profile is not null &&
        Directory.Exists(GamePath) &&
        Directory.Exists(ModsPath);

    private void CreatePreview()
    {
        if (_discovery is null || SelectedMo2Profile is null)
        {
            return;
        }

        try
        {
            var preview = Mo2ImportService.CreatePreview(
                _discovery,
                SelectedMo2Profile,
                GamePath,
                ModsPath,
                OverwritePath);
            ReplacePreview(preview);
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(PreviewSummary));
            IncludeOverwrite = false;
            Step = 2;
            SetMessage(
                preview.AmbiguousModCount > 0
                    ? Strings.Mo2_AmbiguousHint
                    : preview.MissingModCount > 0
                        ? Strings.Mo2_MissingHint
                    : Strings.Mo2_ReviewHint,
                preview.MissingModCount > 0 || preview.AmbiguousModCount > 0);
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, isWarning: true);
        }
    }

    private void Back()
    {
        Step = 1;
        SetMessage(Strings.Mo2_CheckSourceHint);
    }

    private bool CanImport() =>
        Step == 2 &&
        Preview is not null &&
        !string.IsNullOrWhiteSpace(ProfileName) &&
        Preview.FoundModCount > 0 &&
        Preview.AmbiguousModCount == 0;

    private async Task ImportAsync()
    {
        if (Preview is null)
        {
            return;
        }

        try
        {
            var profile = Mo2ImportService.CreateProfile(Preview, ProfileName, IncludeOverwrite);
            if (await _commitProfileAsync(profile))
            {
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, isWarning: true);
        }
    }

    private void SetMessage(string message, bool isWarning = false)
    {
        Message = message;
        IsMessageWarning = isWarning;
    }

    private void ReplacePreview(Mo2ImportPreview? preview)
    {
        if (_preview is not null)
        {
            foreach (var entry in _preview.Entries)
            {
                entry.PropertyChanged -= PreviewEntryOnPropertyChanged;
            }
        }

        _preview = preview;
        if (_preview is not null)
        {
            foreach (var entry in _preview.Entries)
            {
                entry.PropertyChanged += PreviewEntryOnPropertyChanged;
            }
        }
    }

    private void PreviewEntryOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Mo2ImportPreviewEntry.SourcePath))
        {
            return;
        }

        OnPropertyChanged(nameof(PreviewSummary));
        if (Preview?.AmbiguousModCount == 0)
        {
            SetMessage(Strings.Mo2_AmbiguousResolved);
        }

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)NextCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BackCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportCommand).RaiseCanExecuteChanged();
    }
}
