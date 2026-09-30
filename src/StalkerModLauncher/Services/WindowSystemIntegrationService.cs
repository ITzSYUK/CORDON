using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StalkerModLauncher.Services;

public readonly record struct WindowFrameColors(
    byte CaptionRed,
    byte CaptionGreen,
    byte CaptionBlue,
    byte BorderRed,
    byte BorderGreen,
    byte BorderBlue,
    byte TextRed,
    byte TextGreen,
    byte TextBlue);

public static class WindowSystemIntegrationService
{
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public static readonly WindowFrameColors ClassicFrameColors = new(0x0F, 0x11, 0x0D, 0x4A, 0x4E, 0x3A, 0xF0, 0xE8, 0xC8);
    public static readonly WindowFrameColors PdaFrameColors = new(0x08, 0x0C, 0x13, 0x3B, 0x47, 0x55, 0xE7, 0xE3, 0xD3);
    public static readonly WindowFrameColors NewPdaFrameColors = new(0x0C, 0x0D, 0x0C, 0x48, 0x4A, 0x43, 0xDE, 0xDD, 0xD4);

    public static void Initialize(Window window)
    {
        Initialize(window, ClassicFrameColors);
    }

    public static void Initialize(Window window, WindowFrameColors colors)
    {
        ApplyDarkWindowFrame(window, colors);
    }

    private static void ApplyDarkWindowFrame(Window window, WindowFrameColors colors)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = 1;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref useDarkMode, sizeof(int));

        var captionColor = ToColorRef(colors.CaptionRed, colors.CaptionGreen, colors.CaptionBlue);
        var borderColor = ToColorRef(colors.BorderRed, colors.BorderGreen, colors.BorderBlue);
        var textColor = ToColorRef(colors.TextRed, colors.TextGreen, colors.TextBlue);
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref borderColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
