using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ScreenshotsViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly IScreenshotClipboardService _clipboardService;
    private CancellationTokenSource? _statusCancellation;
    private BitmapImage? _selectedScreenshot;
    private bool _isFullScreen;
    private bool _isLoading = true;
    private bool _isDisposed;
    private int _selectedIndex = -1;
    private string _statusText = string.Empty;

    public ScreenshotsViewModel(
        ModProfile profile,
        IScreenshotClipboardService clipboardService)
    {
        _clipboardService = clipboardService;
        OpenScreenshotCommand = new RelayCommand(param =>
        {
            if (param is ScreenshotItem item)
            {
                var index = Screenshots.IndexOf(item);
                if (index >= 0)
                {
                    OpenScreenshot(index);
                }
            }
        });

        CloseFullScreenCommand = new RelayCommand(CloseFullScreen);
        GoPreviousCommand = new RelayCommand(_ => GoPrevious(), _ => CanGoPrevious);
        GoNextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext);
        CopyScreenshotCommand = new RelayCommand(parameter =>
        {
            if (parameter is ScreenshotItem item)
            {
                CopyScreenshot(item);
            }
        });

        _ = LoadScreenshotsAsync(profile);
    }

    public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

    public BitmapImage? SelectedScreenshot
    {
        get => _selectedScreenshot;
        set => SetProperty(ref _selectedScreenshot, value);
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (SetProperty(ref _isFullScreen, value))
            {
                OnPropertyChanged(nameof(ShowThumbnailGrid));
                OnPropertyChanged(nameof(ShowFullScreenView));
                OnPropertyChanged(nameof(HasNoScreenshots));
                RefreshNavigationState();
            }
        }
    }

    public bool ShowThumbnailGrid => !_isFullScreen && Screenshots.Count > 0;
    public bool ShowFullScreenView => _isFullScreen;
    public bool HasNoScreenshots => !IsLoading && Screenshots.Count == 0 && !_isFullScreen;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(HasNoScreenshots));
            }
        }
    }
    public bool CanGoPrevious => _isFullScreen && _selectedIndex > 0;
    public bool CanGoNext => _isFullScreen && _selectedIndex < Screenshots.Count - 1;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand OpenScreenshotCommand { get; }
    public ICommand CloseFullScreenCommand { get; }
    public ICommand GoPreviousCommand { get; }
    public ICommand GoNextCommand { get; }
    public ICommand CopyScreenshotCommand { get; }

    public void OpenScreenshot(int index)
    {
        if (index < 0 || index >= Screenshots.Count)
        {
            return;
        }

        _selectedIndex = index;
        SelectedScreenshot = Screenshots[index].LoadForDisplay();
        if (!_isFullScreen)
        {
            IsFullScreen = true;
        }
        else
        {
            RefreshNavigationState();
        }
    }

    public void CloseFullScreen()
    {
        IsFullScreen = false;
        SelectedScreenshot = null;
        _selectedIndex = -1;
    }

    public void GoPrevious()
    {
        if (_selectedIndex > 0)
        {
            OpenScreenshot(_selectedIndex - 1);
        }
    }

    public void GoNext()
    {
        if (_selectedIndex < Screenshots.Count - 1)
        {
            OpenScreenshot(_selectedIndex + 1);
        }
    }

    public void CopySelectedScreenshot()
    {
        if (_selectedIndex >= 0 && _selectedIndex < Screenshots.Count)
        {
            CopyScreenshot(Screenshots[_selectedIndex]);
        }
    }

    public void HandleKeyDown(System.Windows.Input.Key key)
    {
        if (!_isFullScreen)
        {
            return;
        }

        switch (key)
        {
            case Key.Left:
                GoPrevious();
                break;
            case Key.Right:
                GoNext();
                break;
            case Key.Escape:
                CloseFullScreen();
                break;
        }
    }

    private void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        ((RelayCommand)GoPreviousCommand).RaiseCanExecuteChanged();
        ((RelayCommand)GoNextCommand).RaiseCanExecuteChanged();
    }

    public void CopyScreenshot(ScreenshotItem item)
    {
        try
        {
            _clipboardService.Copy(item.FilePath);
            ShowTemporaryStatus(LocalizedText.Format(Strings.Screenshots_CopiedFormat, Path.GetFileName(item.FilePath)));
        }
        catch (Exception ex)
        {
            ShowTemporaryStatus(LocalizedText.Format(Strings.Screenshots_CopyFailedFormat, ex.Message));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        _statusCancellation?.Cancel();
        _statusCancellation?.Dispose();
        _statusCancellation = null;
        SelectedScreenshot = null;
        Screenshots.Clear();
    }

    private async void ShowTemporaryStatus(string message)
    {
        _statusCancellation?.Cancel();
        _statusCancellation?.Dispose();
        _statusCancellation = new CancellationTokenSource();
        var cancellationToken = _statusCancellation.Token;

        StatusText = message;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            StatusText = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadScreenshotsAsync(ModProfile profile)
    {
        var cancellationToken = _loadCancellation.Token;
        try
        {
            var files = await ScreenshotScannerService.ScanAsync(profile, cancellationToken);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isDisposed)
                {
                    return;
                }

                try
                {
                    Screenshots.Add(new ScreenshotItem(file));
                }
                catch
                {
                    // Ignore unreadable or partially written image files.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!_isDisposed)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowThumbnailGrid));
            }
        }
    }
}
