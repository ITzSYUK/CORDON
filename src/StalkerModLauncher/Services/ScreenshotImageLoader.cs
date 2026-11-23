using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StalkerModLauncher.Services;

public static class ScreenshotImageLoader
{
    private const int ThumbnailDecodeWidth = 480;
    private const int DisplayDecodeWidth = 2560;
    private const int SaveThumbnailWidth = 160;

    public static BitmapImage LoadThumbnail(string filePath)
    {
        return Load(filePath, ThumbnailDecodeWidth);
    }

    internal static BitmapSource? LoadSaveThumbnail(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return Load(filePath, SaveThumbnailWidth, stream);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException)
        {
            stream.Position = 0;
            return LoadUncompressedDds(stream);
        }
    }

    private static BitmapSource? LoadUncompressedDds(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var header = reader.ReadBytes(128);
        if (header.Length != 128)
            return null;

        uint Field(int offset) => BitConverter.ToUInt32(header, offset);
        var width = Field(16);
        var height = Field(12);
        var pitch = Field(20);
        // IX-Ray writes legacy DDS with BGRA pixels. WPF's DDS decoder accepts block-compressed textures.
        if (Field(0) != 0x20534444 || Field(4) != 124 || Field(76) != 32 ||
            Field(80) != 0x41 || Field(88) != 32 ||
            Field(92) != 0x00ff0000 || Field(96) != 0x0000ff00 ||
            Field(100) != 0x000000ff || Field(104) != 0xff000000 ||
            width is 0 or > 2048 || height is 0 or > 2048 || pitch != width * 4)
        {
            return null;
        }

        var byteCount = (int)(pitch * height);
        if (stream.Length - stream.Position < byteCount)
            return null;

        var pixels = reader.ReadBytes(byteCount);
        if (pixels.Length != byteCount)
            return null;

        BitmapSource thumbnail = BitmapSource.Create(
            (int)width, (int)height, 96, 96, PixelFormats.Bgra32, null, pixels, (int)pitch);
        var scale = (double)SaveThumbnailWidth / Math.Max(width, height);
        if (scale < 1)
            thumbnail = new TransformedBitmap(thumbnail, new ScaleTransform(scale, scale));
        thumbnail.Freeze();
        return thumbnail;
    }

    public static BitmapImage LoadForDisplay(string filePath)
    {
        return Load(filePath, DisplayDecodeWidth);
    }

    public static BitmapImage LoadFullResolution(string filePath)
    {
        return Load(filePath, decodePixelWidth: null);
    }

    private static BitmapImage Load(string filePath, int? decodePixelWidth, Stream? stream = null)
    {
        var image = new BitmapImage();
        image.BeginInit();
        if (stream is null)
        {
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        }
        else
        {
            image.StreamSource = stream;
        }
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth is not null)
        {
            image.DecodePixelWidth = decodePixelWidth.Value;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }
}
