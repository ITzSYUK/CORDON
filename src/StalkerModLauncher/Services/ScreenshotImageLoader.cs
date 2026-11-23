using System.Windows.Media.Imaging;

namespace StalkerModLauncher.Services;

public static class ScreenshotImageLoader
{
    private const int ThumbnailDecodeWidth = 480;
    private const int DisplayDecodeWidth = 2560;

    public static BitmapImage LoadThumbnail(string filePath)
    {
        return Load(filePath, ThumbnailDecodeWidth);
    }

    internal static BitmapImage LoadSaveThumbnail(string filePath) => Load(filePath, 160);

    public static BitmapImage LoadForDisplay(string filePath)
    {
        return Load(filePath, DisplayDecodeWidth);
    }

    public static BitmapImage LoadFullResolution(string filePath)
    {
        return Load(filePath, decodePixelWidth: null);
    }

    private static BitmapImage Load(string filePath, int? decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(filePath, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodePixelWidth is not null)
        {
            image.DecodePixelWidth = decodePixelWidth.Value;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }
}
