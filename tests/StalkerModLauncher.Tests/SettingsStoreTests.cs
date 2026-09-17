using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        _store = new SettingsStore(_paths);
    }

    [Fact]
    public async Task LoadAsyncReturnsBackupWhenPrimaryJsonIsCorrupted()
    {
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "first" });
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "second" });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken json");

        var loaded = await _store.LoadAsync();

        Assert.Equal("first", loaded.LastBrowsedGamePath);
        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Equal("first", (await _store.LoadAsync()).LastBrowsedGamePath);
        var recovered = Assert.Single(Directory.GetFiles(Path.Combine(_paths.ConfigDirectory, "recovery")));
        Assert.Contains("{ broken json", await File.ReadAllTextAsync(recovered));
    }

    [Fact]
    public async Task LoadWithRecoveryAsyncReportsBackupRecoveryAndPreservesBrokenPrimary()
    {
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "backup" });
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "primary" });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");

        var result = await _store.LoadWithRecoveryAsync();

        Assert.Equal("backup", result.Settings.LastBrowsedGamePath);
        Assert.Equal(SettingsRecoveryMode.Backup, result.Recovery?.Mode);
        var damaged = Assert.Single(result.Recovery!.Files);
        Assert.Equal(_paths.SettingsFile, damaged.OriginalPath);
        Assert.True(File.Exists(damaged.RecoveryPath));
        Assert.Contains("Некорректный JSON", damaged.Error);

        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "after-recovery" });
        Assert.Equal("after-recovery", (await _store.LoadAsync()).LastBrowsedGamePath);
    }

    [Fact]
    public async Task UpdateAsyncAppliesConcurrentChangesWithoutLosingFields()
    {
        await _store.SaveAsync(new AppSettings());

        await Task.WhenAll(
            _store.UpdateAsync(settings =>
            {
                settings.LastBrowsedGamePath = "game";
                return settings;
            }),
            _store.UpdateAsync(settings =>
            {
                settings.DontShowAboutOnStartup = true;
                return settings;
            }));

        var loaded = await _store.LoadAsync();
        Assert.Equal("game", loaded.LastBrowsedGamePath);
        Assert.True(loaded.DontShowAboutOnStartup);
    }

    [Fact]
    public async Task SaveAndLoadPreservesLauncherPreferences()
    {
        await _store.SaveAsync(new AppSettings
        {
            IsPdaInterfaceEnabled = true,
            UseNewPdaInterface = true,
            ShowTrayIcon = true,
            StartWithWindows = true,
            StartMinimizedToTrayOnWindowsStartup = false,
            MinimizeToTrayOnClose = true,
            AutoCheckForUpdates = false,
            ShowUpdateNotifications = false,
            LogLevel = LauncherLogLevel.Detailed,
            UiLanguage = UiLanguage.English
        });

        var loaded = await _store.LoadAsync();

        Assert.True(loaded.IsPdaInterfaceEnabled);
        Assert.True(loaded.UseNewPdaInterface);
        Assert.True(loaded.ShowTrayIcon);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.StartMinimizedToTrayOnWindowsStartup);
        Assert.True(loaded.MinimizeToTrayOnClose);
        Assert.False(loaded.AutoCheckForUpdates);
        Assert.False(loaded.ShowUpdateNotifications);
        Assert.Equal(LauncherLogLevel.Detailed, loaded.LogLevel);
        Assert.Equal(UiLanguage.English, loaded.UiLanguage);
    }

    [Fact]
    public async Task SaveAsyncCapturesSnapshotBeforeWaitingForWrite()
    {
        var settings = new AppSettings { LastBrowsedGamePath = "snapshot" };
        var save = _store.SaveAsync(settings);
        settings.LastBrowsedGamePath = "mutated";
        await save;

        var loaded = await _store.LoadAsync();
        Assert.Equal("snapshot", loaded.LastBrowsedGamePath);
    }

    [Fact]
    public async Task LoadAsyncReturnsDefaultsWhenPrimaryAndBackupAreCorrupted()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, "{ broken backup");

        var loaded = await _store.LoadAsync();

        Assert.Equal(string.Empty, loaded.LastBrowsedGamePath);
        Assert.Empty(loaded.Profiles);
        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.False(File.Exists(_paths.SettingsBackupFile));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_paths.ConfigDirectory, "recovery")).Length);
    }

    [Fact]
    public async Task LoadWithRecoveryAsyncReportsDefaultsWhenNoValidSettingsRemain()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, "{ broken backup");

        var result = await _store.LoadWithRecoveryAsync();

        Assert.Equal(SettingsRecoveryMode.Defaults, result.Recovery?.Mode);
        Assert.Equal(2, result.Recovery!.Files.Count);
        Assert.All(result.Recovery.Files, file => Assert.True(File.Exists(file.RecoveryPath)));
    }

    [Fact]
    public async Task LoadWithRecoveryAsyncRestoresMissingPrimaryFromBackup()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(
            _paths.SettingsBackupFile,
            """{"LastBrowsedGamePath":"backup"}""");

        var result = await _store.LoadWithRecoveryAsync();

        Assert.Equal("backup", result.Settings.LastBrowsedGamePath);
        Assert.Equal(SettingsRecoveryMode.Backup, result.Recovery?.Mode);
        Assert.Empty(result.Recovery!.Files);
        Assert.True(File.Exists(_paths.SettingsFile));
    }

    [Fact]
    public async Task LoadWithRecoveryAsyncDoesNotReplaceTemporarilyLockedSettings()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """{"LastBrowsedGamePath":"locked"}""");

        await using (var locked = new FileStream(
                         _paths.SettingsFile,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            var error = await Assert.ThrowsAsync<SettingsPersistenceException>(
                () => _store.LoadWithRecoveryAsync());

            Assert.Contains("временно недоступен", error.Message);
            await Assert.ThrowsAsync<SettingsPersistenceException>(
                () => _store.SaveAsync(new AppSettings()));
            Assert.False(Directory.Exists(Path.Combine(_paths.ConfigDirectory, "recovery")));
            Assert.True(locked.Length > 0);
        }

        Assert.Equal("locked", (await _store.LoadWithRecoveryAsync()).Settings.LastBrowsedGamePath);
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "available" });
        Assert.Equal("available", (await _store.LoadAsync()).LastBrowsedGamePath);
    }

    [Fact]
    public async Task LoadWithRecoveryAsyncPreservesRepeatedFailuresWithSameTimestamp()
    {
        var fixedTime = new DateTimeOffset(2026, 8, 2, 12, 34, 56, TimeSpan.Zero);
        var store = new SettingsStore(_paths, new FixedTimeProvider(fixedTime));
        await store.SaveAsync(new AppSettings { LastBrowsedGamePath = "first" });
        await store.SaveAsync(new AppSettings { LastBrowsedGamePath = "second" });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken first");
        await store.LoadWithRecoveryAsync();

        await store.SaveAsync(new AppSettings { LastBrowsedGamePath = "third" });
        await store.SaveAsync(new AppSettings { LastBrowsedGamePath = "fourth" });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken second");
        await store.LoadWithRecoveryAsync();

        var recovered = Directory.GetFiles(Path.Combine(_paths.ConfigDirectory, "recovery"));
        Assert.Equal(2, recovered.Length);
        Assert.Equal(2, recovered.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(
            recovered,
            path => Path.GetFileNameWithoutExtension(path).EndsWith("-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAsyncNotifiesWhenItRecoversSettings()
    {
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "backup" });
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "primary" });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        SettingsRecoveryInfo? reported = null;
        _store.RecoveryCompleted += (_, recovery) => reported = recovery;

        await _store.UpdateAsync(settings =>
        {
            settings.LastBrowsedGamePath = "updated";
            return settings;
        });

        Assert.Equal(SettingsRecoveryMode.Backup, reported?.Mode);
        Assert.Equal("updated", (await _store.LoadAsync()).LastBrowsedGamePath);
    }

    [Fact]
    public async Task HasSettingsFileIsFalseUntilSettingsAreSaved()
    {
        Assert.False(_store.HasSettingsFile);

        await _store.SaveAsync(new AppSettings());

        Assert.True(_store.HasSettingsFile);
    }

    [Fact]
    public async Task SaveAsyncPreservesProfileAndModOrder()
    {
        var first = new ModProfile { Name = "First" };
        first.Mods.Add(new ModEntry { Name = "Low priority", Order = 1 });
        first.Mods.Add(new ModEntry { Name = "High priority", Order = 2 });
        var second = new ModProfile { Name = "Second" };

        await _store.SaveAsync(new AppSettings { Profiles = [second, first] });
        var loaded = await _store.LoadAsync();

        Assert.Equal(["Second", "First"], loaded.Profiles.Select(profile => profile.Name));
        Assert.Equal(
            ["Low priority", "High priority"],
            loaded.Profiles[1].Mods.Select(mod => mod.Name));
    }

    [Fact]
    public async Task SaveAndLoadAsyncPreservesPerProfileDiscordStatus()
    {
        var profile = new ModProfile { Name = "No Discord", IsDiscordStatusEnabled = false };

        await _store.SaveAsync(new AppSettings { Profiles = [profile] });
        var loaded = await _store.LoadAsync();

        Assert.False(loaded.Profiles.Single().IsDiscordStatusEnabled);
    }

    [Fact]
    public async Task LoadAsyncMigratesLegacyGlobalGamePath()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """{"GameInstallPath":"D:\\Games\\STALKER","Profiles":[]}""");

        var loaded = await _store.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(@"D:\Games\STALKER", loaded.LastBrowsedGamePath);
        Assert.Null(loaded.LegacyGameInstallPath);
    }

    [Fact]
    public async Task SaveAsyncDoesNotPersistRuntimeProperties()
    {
        var profile = new ModProfile { IsRunning = true };
        profile.Mods.Add(new ModEntry { HasOverlapsAbove = true });

        await _store.SaveAsync(new AppSettings { Profiles = [profile] });
        var json = await File.ReadAllTextAsync(_paths.SettingsFile);

        Assert.DoesNotContain("\"IsRunning\"", json);
        Assert.DoesNotContain("\"HasOverlapsAbove\"", json);
        Assert.DoesNotContain("\"PlaytimeDisplay\"", json);
        Assert.DoesNotContain("\"LastPlayedDisplay\"", json);
    }

    [Fact]
    public async Task LoadAsyncRepairsDuplicateIdsAndModOrder()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """
            {
              "Profiles": [
                { "Id": "same", "Mods": [{ "Id": "mod", "Order": 8 }, { "Id": "mod", "Order": 3 }] },
                { "Id": "same", "Mods": [] }
              ]
            }
            """);

        var loaded = await _store.LoadAsync();

        Assert.NotEqual(loaded.Profiles[0].Id, loaded.Profiles[1].Id);
        Assert.NotEqual(loaded.Profiles[0].Mods[0].Id, loaded.Profiles[0].Mods[1].Id);
        Assert.Equal([1, 2], loaded.Profiles[0].Mods.Select(mod => mod.Order));
    }

    [Fact]
    public async Task SaveAndLoadAsyncHandlesLargeProfileCollection()
    {
        var settings = new AppSettings();
        for (var profileIndex = 0; profileIndex < 100; profileIndex++)
        {
            var profile = new ModProfile { Name = $"Profile {profileIndex}" };
            for (var modIndex = 0; modIndex < 50; modIndex++)
            {
                profile.Mods.Add(new ModEntry
                {
                    Name = $"Mod {modIndex}",
                    SourcePath = $@"D:\Mods\Profile-{profileIndex}\Mod-{modIndex}",
                    Order = modIndex + 1
                });
            }

            settings.Profiles.Add(profile);
        }

        await _store.SaveAsync(settings);
        var loaded = await _store.LoadAsync();

        Assert.Equal(100, loaded.Profiles.Count);
        Assert.All(loaded.Profiles, profile => Assert.Equal(50, profile.Mods.Count));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => value;
    }
}
