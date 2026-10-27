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
    public void ReadPreferenceDoesNotCrashOnMalformedJsonTypes()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsBackupFile, """{"SchemaVersion":9,"UiLanguage":"en"}""");
        foreach (var damaged in new[] { "{}", "[]", "null", "\"text\"", "{\"SchemaVersion\":\"10\"}", "{\"SchemaVersion\":null}", "{\"SchemaVersion\":true}" })
        {
            File.WriteAllText(paths.SettingsFile, damaged);
            Assert.Equal(UiLanguage.English, UiLanguage.ReadPreference(paths));
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPreferenceUsesValidatedPendingLanguageWithoutWriting(bool portable)
    {
        var paths = portable ? new AppPaths(_root, useLocalSettings: true) : CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        const string index = """{"SchemaVersion":10,"UiLanguage":"en","ProfileIds":[]}""";
        const string journal = """{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[]}""";
        File.WriteAllText(paths.SettingsFile, index);
        File.WriteAllText(paths.SettingsPendingFile, journal);

        Assert.Equal(UiLanguage.Russian, UiLanguage.ReadPreference(paths));
        Assert.Equal(index, File.ReadAllText(paths.SettingsFile));
        Assert.Equal(journal, File.ReadAllText(paths.SettingsPendingFile));
        Assert.False(Directory.Exists(paths.ProfilesDirectory));
        using var store = new SettingsStore(paths);
        Assert.Equal(UiLanguage.Russian, (await store.LoadAsync()).UiLanguage);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"SchemaVersion":11,"UiLanguage":"ru","Profiles":[]}""")]
    [InlineData("""{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[{}]}""")]
    [InlineData("""{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[{"Id":"same"},{"Id":"same"}]}""")]
    [InlineData("""{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[{"Id":"same"}],"DeletedProfileIds":["same"]}""")]
    [InlineData("""{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[],"UnavailableProfileIds":["unknown"]}""")]
    [InlineData("""{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[{"Id":"test","TotalPlaytimeSeconds":-1}]}""")]
    public void ReadPreferenceIgnoresInvalidJournalWithoutChangingIt(string journal)
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":10,"UiLanguage":"en","ProfileIds":[]}""");
        File.WriteAllText(paths.SettingsPendingFile, journal);

        Assert.Equal(UiLanguage.English, UiLanguage.ReadPreference(paths));
        Assert.Equal(journal, File.ReadAllText(paths.SettingsPendingFile));
        Assert.False(Directory.Exists(paths.ProfilesDirectory));
    }

    [Fact]
    public void ReadPreferenceIgnoresJournalWithEscapingPortablePath()
    {
        var paths = new AppPaths(_root, useLocalSettings: true);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":10,"UiLanguage":"en","ProfileIds":[]}""");
        const string journal = """{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[{"Id":"test","GameInstallPath":"../outside"}]}""";
        File.WriteAllText(paths.SettingsPendingFile, journal);

        Assert.Equal(UiLanguage.English, UiLanguage.ReadPreference(paths));
        Assert.Equal(journal, File.ReadAllText(paths.SettingsPendingFile));
        Assert.False(Directory.Exists(paths.ProfilesDirectory));
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
