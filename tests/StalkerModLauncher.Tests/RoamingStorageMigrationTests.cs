using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class RoamingStorageMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonMigrationTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _source;
    private readonly AppPaths _destination;

    public RoamingStorageMigrationTests()
    {
        _source = new AppPaths(Path.Combine(_root, "StalkerModLauncher"), Path.Combine(_root, "workspaces"), false);
        _destination = new AppPaths(Path.Combine(_root, "launcher"), useLocalSettings: false,
            Path.Combine(_root, "CordonModLauncher"), _source.ConfigDirectory);
    }

    [Fact]
    public void RegularStorageUsesCordonNameAndKeepsWorkspaceLocations()
    {
        var paths = new AppPaths(_root, useLocalSettings: false);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CordonModLauncher"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StalkerModLauncher"), paths.LegacyConfigDirectory);
        Assert.Equal(@"Z:\StalkerModLauncher\Workspaces", paths.GetPreferredWorkspaceRoot(@"Z:\game"));
        Assert.Equal(@"Z:\StalkerModLauncher\Mods", paths.GetDefaultModInstallPath(@"Z:\game"));
    }

    [Theory]
    [InlineData(false, LaunchBackendKind.LegacyWorkspace)]
    [InlineData(false, LaunchBackendKind.VirtualFileSystem)]
    [InlineData(true, LaunchBackendKind.LegacyWorkspace)]
    [InlineData(true, LaunchBackendKind.VirtualFileSystem)]
    public async Task MigratesBothFormatsOnceWithoutChangingSource(bool split, LaunchBackendKind backend)
    {
        var profile = new ModProfile
        {
            Name = "Migrated",
            LaunchBackendKind = backend,
            GameInstallPath = @"D:\game",
            WorkspacePath = @"D:\workspace",
            TotalPlaytimeSeconds = 123,
            LaunchArguments = "-nointro"
        };
        profile.Mods.Add(new ModEntry { Name = "Mod", SourcePath = @"D:\mods\one", Order = 1 });
        var settings = new AppSettings { SchemaVersion = 9, UiLanguage = "en", IsPdaInterfaceEnabled = true, Profiles = [profile] };
        Directory.CreateDirectory(_source.ConfigDirectory);
        if (split)
        {
            using var source = new SettingsStore(_source);
            await source.SaveAsync(settings);
            await source.SaveAsync(settings);
            await File.WriteAllTextAsync(Path.Combine(_source.ConfigDirectory, "settings.legacy.json"), "archive");
        }
        else await File.WriteAllTextAsync(_source.SettingsFile, JsonSerializer.Serialize(settings));
        await File.WriteAllTextAsync(Path.Combine(_source.ConfigDirectory, "launcher.log"), "old log");
        var original = SnapshotSource();
        using var destination = new SettingsStore(_destination);

        Assert.Equal("en", UiLanguage.ReadPreference(_destination));
        Assert.True(await destination.TryImportRoamingSettingsAsync());
        var loaded = await destination.LoadAsync();
        var migrated = Assert.Single(loaded.Profiles);
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(migrated));
        Assert.Equal("en", loaded.UiLanguage);
        Assert.True(loaded.IsPdaInterfaceEnabled);
        Assert.True(File.Exists(_destination.GetProfileFile(profile.Id)));
        Assert.False(File.Exists(Path.Combine(_destination.ConfigDirectory, "launcher.log")));
        Assert.Equal(original, SnapshotSource());
        if (split)
        {
            Assert.Equal("archive", await File.ReadAllTextAsync(Path.Combine(_destination.ConfigDirectory, "settings.legacy.json")));
            Assert.Equal(await File.ReadAllTextAsync(_source.SettingsBackupFile), await File.ReadAllTextAsync(_destination.SettingsBackupFile));
        }

        // Simulate an old launcher overwriting its own store after migration.
        await File.WriteAllTextAsync(_source.SettingsFile, """{"SchemaVersion":9,"Profiles":[]}""");
        using var restarted = new SettingsStore(_destination);
        Assert.False(await restarted.TryImportRoamingSettingsAsync());
        Assert.Equal(profile.Id, Assert.Single((await restarted.LoadAsync()).Profiles).Id);
    }

    [Fact]
    public async Task RecoversPendingSaveOnlyInDestination()
    {
        Directory.CreateDirectory(_source.ConfigDirectory);
        var profile = new ModProfile { Name = "Pending", TotalPlaytimeSeconds = 42 };
        var journal = JsonSerializer.Serialize(new AppSettings { Profiles = [profile], UiLanguage = "ru" });
        await File.WriteAllTextAsync(_source.SettingsPendingFile, journal);
        var original = SnapshotSource();
        using var destination = new SettingsStore(_destination);

        Assert.Equal("ru", UiLanguage.ReadPreference(_destination));
        Assert.Equal(original, SnapshotSource());
        var loaded = await destination.LoadAsync();

        Assert.Equal(profile.Id, Assert.Single(loaded.Profiles).Id);
        Assert.Equal("ru", loaded.UiLanguage);
        Assert.False(File.Exists(_destination.SettingsPendingFile));
        Assert.Equal(original, SnapshotSource());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoversMissingIndexFromBackupOrProfileFiles(bool keepBackup)
    {
        using var source = new SettingsStore(_source);
        var profile = new ModProfile { Name = "Recover" };
        var settings = new AppSettings { Profiles = [profile], UiLanguage = "en" };
        await source.SaveAsync(settings);
        await source.SaveAsync(settings);
        File.Delete(_source.SettingsFile);
        if (!keepBackup) File.Delete(_source.SettingsBackupFile);
        var original = SnapshotSource();
        using var destination = new SettingsStore(_destination);

        Assert.Equal(profile.Id, Assert.Single((await destination.LoadAsync()).Profiles).Id);
        Assert.Equal(original, SnapshotSource());
    }

    [Fact]
    public async Task InvalidSourceJournalBlocksDestinationWritesAndPreservesBothCopies()
    {
        Directory.CreateDirectory(_source.ConfigDirectory);
        await File.WriteAllTextAsync(_source.SettingsPendingFile, "{broken");
        using var destination = new SettingsStore(_destination);

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.LoadAsync());
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.SaveAsync(new AppSettings()));
        Assert.Equal("{broken", await File.ReadAllTextAsync(_source.SettingsPendingFile));
        Assert.Equal("{broken", await File.ReadAllTextAsync(_destination.SettingsPendingFile));
        Assert.False(File.Exists(_destination.SettingsFile));
    }

    [Fact]
    public async Task PreservesMissingAndDamagedProfilesAndRecoversBackupLocally()
    {
        var healthy = new ModProfile { Name = "Healthy" };
        var recovered = new ModProfile { Name = "Backup" };
        var missing = new ModProfile { Name = "Missing" };
        using var source = new SettingsStore(_source);
        var settings = new AppSettings { Profiles = [healthy, recovered, missing] };
        await source.SaveAsync(settings);
        recovered.Description = "changed";
        await source.SaveAsync(settings);
        await File.WriteAllTextAsync(_source.GetProfileFile(recovered.Id), "{broken");
        File.Delete(_source.GetProfileFile(missing.Id));
        var recovery = Path.Combine(_source.ConfigDirectory, "recovery");
        Directory.CreateDirectory(recovery);
        await File.WriteAllTextAsync(Path.Combine(recovery, "old.json"), "old damage");
        var original = SnapshotSource();
        using var destination = new SettingsStore(_destination);

        var loaded = await destination.LoadWithRecoveryAsync();

        Assert.Equal(new[] { healthy.Id, recovered.Id, missing.Id }, loaded.Settings.Profiles.Select(p => p.Id));
        Assert.Equal("Backup", loaded.Settings.Profiles[1].Name);
        Assert.Empty(loaded.Settings.Profiles[1].Description);
        Assert.NotEmpty(loaded.Settings.Profiles[2].SettingsLoadError);
        Assert.Equal("old damage", await File.ReadAllTextAsync(Path.Combine(_destination.ConfigDirectory, "recovery", "old.json")));
        Assert.Equal(original, SnapshotSource());
        await destination.SaveAsync(loaded.Settings);
        Assert.False(File.Exists(_destination.GetProfileFile(missing.Id)));
    }

    [Fact]
    public async Task ResumesPublishingFrozenCopyAfterInterruption()
    {
        using var source = new SettingsStore(_source);
        var profile = new ModProfile { Name = "Frozen" };
        await source.SaveAsync(new AppSettings { Profiles = [profile] });
        Directory.CreateDirectory(_destination.ConfigDirectory);
        await File.WriteAllTextAsync(_destination.ProfilesDirectory, "blocks publication");
        using (var destination = new SettingsStore(_destination))
        {
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.TryImportRoamingSettingsAsync());
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.SaveAsync(new AppSettings()));
        }
        await File.WriteAllTextAsync(_source.SettingsFile, "{}");
        File.Delete(_destination.ProfilesDirectory);
        using var restarted = new SettingsStore(_destination);

        Assert.Equal(profile.Id, Assert.Single((await restarted.LoadAsync()).Profiles).Id);
        Assert.False(Directory.Exists(Path.Combine(_destination.ConfigDirectory, "settings-migration")));
    }

    [Fact]
    public async Task RetriesIncompleteCopyWithoutTreatingItAsInitialized()
    {
        using var source = new SettingsStore(_source);
        var profile = new ModProfile { Name = "Retry" };
        await source.SaveAsync(new AppSettings { Profiles = [profile] });
        var staging = Path.Combine(_destination.ConfigDirectory, "settings-migration");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "settings.json"), "partial copy");
        using var destination = new SettingsStore(_destination);

        Assert.Equal(profile.Id, Assert.Single((await destination.LoadAsync()).Profiles).Id);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("backup")]
    [InlineData("pending")]
    [InlineData("profile")]
    public async Task ExistingDestinationPreventsImport(string kind)
    {
        Directory.CreateDirectory(_source.ConfigDirectory);
        await File.WriteAllTextAsync(_source.SettingsFile, """{"SchemaVersion":9,"Profiles":[]}""");
        var path = kind switch
        {
            "index" => _destination.SettingsFile,
            "backup" => _destination.SettingsBackupFile,
            "pending" => _destination.SettingsPendingFile,
            _ => _destination.GetProfileFile("existing")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "keep");
        using var destination = new SettingsStore(_destination);

        Assert.False(await destination.TryImportRoamingSettingsAsync());
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandalonePrefersNewRoamingStoreAndFallsBackToLegacy(bool newStoreExists)
    {
        using var legacy = new SettingsStore(_source);
        await legacy.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Legacy" }] });
        if (newStoreExists)
        {
            using var current = new SettingsStore(new AppPaths(_destination.ConfigDirectory, _source.WorkspaceRoot));
            await current.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Current" }] });
        }
        var paths = new AppPaths(Path.Combine(_root, "standalone"), useLocalSettings: true,
            _destination.ConfigDirectory, _source.ConfigDirectory);
        using var standalone = new SettingsStore(paths);

        Assert.True(await standalone.TryImportRoamingSettingsAsync());
        Assert.Equal(newStoreExists ? "Current" : "Legacy", Assert.Single((await standalone.LoadAsync()).Profiles).Name);
        Assert.Equal(Path.Combine(_root, "standalone", "Data", "StalkerModLauncher"), paths.ConfigDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneImportsBackupOnlyStoreWithoutChangingSource(bool newStore)
    {
        var sourcePaths = newStore ? new AppPaths(_destination.ConfigDirectory, _source.WorkspaceRoot) : _source;
        using var source = new SettingsStore(sourcePaths);
        var profile = new ModProfile { Name = "Backup", TotalPlaytimeSeconds = 42 };
        var settings = new AppSettings { UiLanguage = "en", Profiles = [profile] };
        await source.SaveAsync(settings);
        await source.SaveAsync(settings);
        File.Delete(sourcePaths.SettingsFile);
        var backup = await File.ReadAllTextAsync(sourcePaths.SettingsBackupFile);
        var profileJson = await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id));
        var paths = new AppPaths(Path.Combine(_root, "standalone"), useLocalSettings: true,
            _destination.ConfigDirectory, _source.ConfigDirectory);
        using var standalone = new SettingsStore(paths);

        Assert.Equal("en", UiLanguage.ReadPreference(paths));
        Assert.True(await standalone.TryImportRoamingSettingsAsync());
        var loaded = await standalone.LoadAsync();
        Assert.Equal(profile.Id, Assert.Single(loaded.Profiles).Id);
        Assert.Equal(42, loaded.Profiles[0].TotalPlaytimeSeconds);
        Assert.Equal("en", loaded.UiLanguage);
        Assert.False(File.Exists(sourcePaths.SettingsFile));
        Assert.Equal(backup, await File.ReadAllTextAsync(sourcePaths.SettingsBackupFile));
        Assert.Equal(profileJson, await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id)));
        Assert.False(await standalone.TryImportRoamingSettingsAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StandalonePendingSourceBlocksImportLoadAndWrites(bool hasIndex, bool validJournal)
    {
        Directory.CreateDirectory(_destination.ConfigDirectory);
        if (hasIndex)
            await File.WriteAllTextAsync(_destination.SettingsFile, """{"SchemaVersion":10,"ProfileIds":[]}""");
        var journal = validJournal ? """{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[]}""" : "{broken";
        await File.WriteAllTextAsync(_destination.SettingsPendingFile, journal);
        var paths = new AppPaths(Path.Combine(_root, "standalone"), useLocalSettings: true,
            _destination.ConfigDirectory, _source.ConfigDirectory);
        using var standalone = new SettingsStore(paths);

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => standalone.TryImportRoamingSettingsAsync());
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => standalone.LoadAsync());
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => standalone.SaveAsync(new AppSettings()));
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => standalone.UpdateAsync(current => current));
        Assert.False(standalone.HasSettingsFile);
        Assert.Equal(journal, await File.ReadAllTextAsync(_destination.SettingsPendingFile));
        Assert.Equal(hasIndex, File.Exists(_destination.SettingsFile));
    }

    [Fact]
    public async Task InterruptedMigrationReadsLanguageFromFrozenJournalBeforePublishing()
    {
        var staging = Path.Combine(_destination.ConfigDirectory, "settings-migration");
        Directory.CreateDirectory(staging);
        const string journal = """{"SchemaVersion":10,"UiLanguage":"ru","Profiles":[]}""";
        await File.WriteAllTextAsync(Path.Combine(staging, "settings.pending.json"), journal);
        await File.WriteAllTextAsync(Path.Combine(staging, ".ready"), "");
        await File.WriteAllTextAsync(_destination.SettingsFile, """{"SchemaVersion":10,"UiLanguage":"en","ProfileIds":[]}""");

        Assert.Equal("ru", UiLanguage.ReadPreference(_destination));
        Assert.Equal(journal, await File.ReadAllTextAsync(Path.Combine(staging, "settings.pending.json")));
        using var destination = new SettingsStore(_destination);
        Assert.Equal("ru", (await destination.LoadAsync()).UiLanguage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualImportReadsProfilesWithoutChangingSource(bool split)
    {
        var old = new ModProfile { Name = "Old", LaunchArguments = "-old" };
        Directory.CreateDirectory(_source.ConfigDirectory);
        if (split)
        {
            using var store = new SettingsStore(_source);
            await store.SaveAsync(new AppSettings { Profiles = [old] });
        }
        else await File.WriteAllTextAsync(_source.SettingsFile,
            JsonSerializer.Serialize(new AppSettings { SchemaVersion = 9, Profiles = [old] }));
        var before = SnapshotSource();
        using var destination = new SettingsStore(_destination);

        var read = await destination.ReadLegacyProfilesAsync();

        Assert.Equal(old.Id, Assert.Single(read).Id);
        Assert.Equal("-old", read[0].LaunchArguments);
        Assert.Equal(before, SnapshotSource());
    }

    [Fact]
    public async Task ManualImportRejectsPendingSourceWithoutChangingIt()
    {
        Directory.CreateDirectory(_source.ConfigDirectory);
        await File.WriteAllTextAsync(_source.SettingsPendingFile, "pending");
        using var destination = new SettingsStore(_destination);
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.ReadLegacyProfilesAsync());
        Assert.Equal("pending", await File.ReadAllTextAsync(_source.SettingsPendingFile));
    }

    private KeyValuePair<string, string>[] SnapshotSource() => Directory.EnumerateFiles(_source.ConfigDirectory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => new KeyValuePair<string, string>(Path.GetRelativePath(_source.ConfigDirectory, path),
            Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
