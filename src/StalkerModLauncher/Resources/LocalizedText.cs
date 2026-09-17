using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace StalkerModLauncher.Resources;

public static class LocalizedText
{
    [SuppressMessage("Performance", "CA1863:Use 'CompositeFormat'")]
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}
