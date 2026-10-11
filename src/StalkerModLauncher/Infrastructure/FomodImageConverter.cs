using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace StalkerModLauncher.Infrastructure;

/// <summary>
/// Loads a FOMOD option image from disk with decode and size limits.
/// Missing, oversized or unreadable files collapse to no image.
/// </summary>
public sealed class FomodImageConverter : IValueConverter
{
    private const long MaximumFileBytes = 8L * 1024 * 1024;
    private const int MaximumDecodeDimension = 480;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return DependencyProperty.UnsetValue;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return DependencyProperty.UnsetValue;
            }

            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight)
                image.DecodePixelWidth = Math.Min(MaximumDecodeDimension, frame.PixelWidth);
            else
                image.DecodePixelHeight = Math.Min(MaximumDecodeDimension, frame.PixelHeight);
            stream.Position = 0;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return DependencyProperty.UnsetValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DependencyProperty.UnsetValue;
        }
        catch (NotSupportedException)
        {
            return DependencyProperty.UnsetValue;
        }
        catch (ArgumentException)
        {
            return DependencyProperty.UnsetValue;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
