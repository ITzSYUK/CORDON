using System.Globalization;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class UiLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"StalkerModLauncher-UiLanguage-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(UiLanguage.English, "en-US")]
    [InlineData(UiLanguage.Russian, "ru-RU")]
    [InlineData(UiLanguage.System, "uk-UA")]
    [InlineData("invalid", "uk-UA")]
    public void ResolveReturnsRequestedOrSystemCulture(string value, string expected)
    {
        var culture = UiLanguage.Resolve(value, CultureInfo.GetCultureInfo("uk-UA"));

        Assert.Equal(expected, culture.Name);
    }

    [Fact]
    public void ReadPreferenceUsesBackupWhenPrimaryIsDamaged()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsFile, "{ broken");
        File.WriteAllText(paths.SettingsBackupFile, $$"""{"SchemaVersion":9,"UiLanguage":"en"}""");

        Assert.Equal(UiLanguage.English, UiLanguage.ReadPreference(paths));
    }

    [Fact]
    public void ReadPreferenceKeepsRussianForOldSettings()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsFile, $$"""{"SchemaVersion":8}""");

        Assert.Equal(UiLanguage.Russian, UiLanguage.ReadPreference(paths));
    }

    [Fact]
    public void ReadPreferenceUsesSystemForNonStringLanguage()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsFile, $$"""{"SchemaVersion":9,"UiLanguage":42}""");

        Assert.Equal(UiLanguage.System, UiLanguage.ReadPreference(paths));
    }

    [Fact]
    public void ReadPreferenceUsesRoamingSettingsForFirstPortableStart()
    {
        var executable = Path.Combine(_root, "portable");
        var roaming = Path.Combine(_root, "roaming");
        var paths = new AppPaths(executable, useLocalSettings: true, roaming);
        Directory.CreateDirectory(roaming);
        File.WriteAllText(paths.RoamingSettingsFile, $$"""{"SchemaVersion":9,"UiLanguage":"ru"}""");

        Assert.Equal(UiLanguage.Russian, UiLanguage.ReadPreference(paths));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths CreatePaths() => new(Path.Combine(_root, "config"), Path.Combine(_root, "workspace"));
}
