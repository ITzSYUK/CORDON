using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StalkerModLauncher.Themes;

public static class PdaThemeSelector
{
    private static BitmapSource? _originalDrawerAtlas;
    private static BitmapSource? _neutralDrawerAtlas;

    // ponytail: process-wide selection is sufficient while the app supports one PDA window.
    public static bool UseNewTheme { get; set; }

    public static Uri CurrentSource => new(
        UseNewTheme
            ? "/CORDON;component/Themes/NewPdaTheme.xaml"
            : "/CORDON;component/Themes/PdaTheme.xaml",
        UriKind.RelativeOrAbsolute);

    public static BitmapSource CurrentDrawerAtlas => UseNewTheme
        ? _neutralDrawerAtlas ??= CreateDrawerAtlas(neutral: true)
        : _originalDrawerAtlas ??= CreateDrawerAtlas(neutral: false);

    private static BitmapSource CreateDrawerAtlas(bool neutral)
    {
        var source = new BitmapImage(new Uri(
            "pack://application:,,,/CORDON;component/Resources/PdaDialogAtlas.png",
            UriKind.Absolute));
        source.Freeze();
        if (!neutral)
        {
            return source;
        }

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var gray = (byte)((pixels[offset] * 11 + pixels[offset + 1] * 59 + pixels[offset + 2] * 30) / 100);
            pixels[offset] = gray;
            pixels[offset + 1] = gray;
            pixels[offset + 2] = gray;
        }

        var converted = BitmapSource.Create(
            bgra.PixelWidth,
            bgra.PixelHeight,
            bgra.DpiX,
            bgra.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        converted.Freeze();
        return converted;
    }
}
