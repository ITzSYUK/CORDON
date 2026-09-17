using System.Globalization;
using System.Runtime.CompilerServices;

namespace StalkerModLauncher.Tests;

internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
