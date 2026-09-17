using System.Collections.ObjectModel;
using System.Windows;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ActivityLogViewModel : ObservableObject
{
    private readonly ApplicationLogService _applicationLogService;
    private readonly Action _scheduleSave;
    private string _text = string.Empty;
    private bool _isVisible = true;

    public ActivityLogViewModel(ApplicationLogService applicationLogService, Action scheduleSave)
    {
        _applicationLogService = applicationLogService;
        _scheduleSave = scheduleSave;
        ToggleCommand = new RelayCommand(() => IsVisible = !IsVisible);
    }

    public ObservableCollection<string> Entries { get; } = new();

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(RowHeight));
                _scheduleSave();
            }
        }
    }

    public string ToggleText => IsVisible ? Strings.Log_Hide : Strings.Log_Show;
    public GridLength RowHeight => IsVisible ? new GridLength(125) : new GridLength(0);
    public RelayCommand ToggleCommand { get; }

    public void Load(IEnumerable<string> entries, bool isVisible)
    {
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        _isVisible = isVisible;
        RebuildText();
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(RowHeight));
    }

    public void Append(string message, LauncherLogLevel level = LauncherLogLevel.Standard)
    {
        var entry = _applicationLogService.Write(message, messageLevel: level);
        Entries.Insert(0, entry);
        while (Entries.Count > 200)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        RebuildText();
    }

    private void RebuildText()
    {
        Text = string.Join(Environment.NewLine, Entries);
    }
}
