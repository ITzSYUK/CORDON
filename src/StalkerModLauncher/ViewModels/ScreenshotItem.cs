using System.Windows.Media.Imaging;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ScreenshotItem : ObservableObject
{
    private static readonly SemaphoreSlim LoadLimit = new(2, 2);
    private BitmapImage? _thumbnail;
    private bool _visible;
    private int _generation;

    public string FilePath { get; }
    public BitmapImage? Thumbnail { get => _thumbnail; private set => SetProperty(ref _thumbnail, value); }

    public ScreenshotItem(string filePath)
    {
        FilePath = filePath;
    }

    public async void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        var generation = ++_generation;
        if (!visible) { Thumbnail = null; return; }
        try
        {
            await LoadLimit.WaitAsync();
            BitmapImage image;
            try
            {
                if (!_visible || generation != _generation) return;
                image = await Task.Run(() => ScreenshotImageLoader.LoadThumbnail(FilePath));
            }
            finally { LoadLimit.Release(); }
            if (_visible && generation == _generation) Thumbnail = image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException) { }
    }

    public BitmapImage LoadForDisplay() => ScreenshotImageLoader.LoadForDisplay(FilePath);
}
