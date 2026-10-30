using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class SettingsStoreProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonProfileStorageTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;

    public SettingsStoreProfileTests()
    {
        _paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        _store = new SettingsStore(_paths);
    }

    [Theory]
    [InlineData(LaunchBackendKind.LegacyWorkspace, false)]
    [InlineData(LaunchBackendKind.VirtualFileSystem, false)]
    [InlineData(LaunchBackendKind.LegacyWorkspace, true)]
    public async Task StoresAllProfileSettingsSeparately(LaunchBackendKind backend, bool standalone)
    {
        var profile = new ModProfile
        {
            Name = "Profile",
            Description = "Description",
            LaunchBackendKind = backend,
            IsStandalone = standalone,
            LaunchArguments = "-nointro -nosplash",
            IsDiscordStatusEnabled = false,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            ExecutableSourcePath = @"D:\engine",
            FsgameSourcePath = @"D:\game\fsgame.ltx",
            WorkingDirectoryRelative = "bin_x64",
            GameInstallPath = @"D:\game",
            ModInstallPath = @"D:\mods",
            WorkspacePath = @"D:\workspaces\test",
            Mo2OverwritePath = @"D:\mo2\overwrite",
            TotalPlaytimeSeconds = 1234,
            LastPlayedAt = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc),
            UseBaseGameData = true,
            CollapsedModGroups = ["Patches"]
        };
        profile.Mods.Add(new ModEntry
        {
            Name = "Patch",
            SourcePath = @"D:\mods\patch",
            GroupName = "Patches",
            IsEnabled = false,
            ExcludedFiles = [@"gamedata\test.ltx"],
            Order = 1
        });
        await _store.SaveAsync(new AppSettings { Profiles = [profile], UiLanguage = "en" });

        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.False(index.RootElement.TryGetProperty("Profiles", out _));
        Assert.Equal(profile.Id, index.RootElement.GetProperty("ProfileIds")[0].GetString());
        Assert.Equal("en", index.RootElement.GetProperty("UiLanguage").GetString());
        Assert.False(File.Exists(_paths.SettingsPendingFile));
        var stored = JsonSerializer.Deserialize<ModProfile>(await File.ReadAllTextAsync(_paths.GetProfileFile(profile.Id)));
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(stored));
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(Assert.Single((await _store.LoadAsync()).Profiles)));
    }

    [Fact]
    public async Task MigratesLegacySettingsOnceAndKeepsOriginalBackup()
    {
        Directory.CreateDirectory(_root);
        const string legacy = """{"SchemaVersion":9,"UiLanguage":"en","Profiles":[{"Id":"old","Name":"Original","LaunchArguments":"-test","TotalPlaytimeSeconds":90}]}""";
        await File.WriteAllTextAsync(_paths.SettingsFile, legacy);

        var loaded = await _store.LoadAsync();
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal("old", profile.Id);
        Assert.Equal("-test", profile.LaunchArguments);
        Assert.Equal(90, profile.TotalPlaytimeSeconds);
        Assert.Equal("en", loaded.UiLanguage);
        Assert.Equal(legacy, await File.ReadAllTextAsync(Path.Combine(_root, "settings.legacy.json")));
        Assert.True(File.Exists(_paths.GetProfileFile(profile.Id)));

        profile.Name = "Renamed";
        await _store.SaveAsync(loaded);
        await _store.LoadAsync();
        Assert.Equal(legacy, await File.ReadAllTextAsync(Path.Combine(_root, "settings.legacy.json")));
        Assert.Single(Directory.GetFiles(_paths.ProfilesDirectory, "*.json"), path => !path.EndsWith(".backup.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoversOneDamagedProfileWithoutRollingBackOtherProfilesOrPreferences()
    {
        var first = new ModProfile { Name = "First backup" };
        var second = new ModProfile { Name = "Second backup" };
        var settings = new AppSettings { Profiles = [first, second], LastBrowsedGamePath = "backup" };
        await _store.SaveAsync(settings);
        first.Name = "First current";
        second.Name = "Second current";
        settings.LastBrowsedGamePath = "current";
        await _store.SaveAsync(settings);
        var index = await File.ReadAllTextAsync(_paths.SettingsFile);
        var damagedPath = _paths.GetProfileFile(first.Id);
        await File.WriteAllTextAsync(damagedPath, "{ broken profile");

        var result = await _store.LoadWithRecoveryAsync();

        Assert.Equal(SettingsRecoveryMode.Backup, result.Recovery?.Mode);
        Assert.Equal("First backup", result.Settings.Profiles[0].Name);
        Assert.Equal("Second current", result.Settings.Profiles[1].Name);
        Assert.Equal("current", result.Settings.LastBrowsedGamePath);
        Assert.Equal(index, await File.ReadAllTextAsync(_paths.SettingsFile));
        var preserved = Assert.Single(result.Recovery!.Files);
        Assert.Equal(damagedPath, preserved.OriginalPath);
        Assert.Equal("{ broken profile", await File.ReadAllTextAsync(preserved.RecoveryPath));
        Assert.Null((await _store.LoadWithRecoveryAsync()).Recovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDamagedProfileRemainsUnavailableWithoutBlockingHealthyProfiles(bool corrupt)
    {
        var profile = new ModProfile { Name = "Keep" };
        var healthy = new ModProfile { Name = "Healthy" };
        await _store.SaveAsync(new AppSettings { Profiles = [profile, healthy] });
        var path = _paths.GetProfileFile(profile.Id);
        var original = await File.ReadAllTextAsync(path);
        var index = await File.ReadAllTextAsync(_paths.SettingsFile);
        if (corrupt) await File.WriteAllTextAsync(path, "{ broken");
        else File.Delete(path);

        var loaded = await _store.LoadAsync();
        Assert.Equal([profile.Id, healthy.Id], loaded.Profiles.Select(item => item.Id));
        Assert.NotEmpty(loaded.Profiles[0].SettingsLoadError);
        Assert.Empty(loaded.Profiles[1].SettingsLoadError);
        Assert.Equal(index, await File.ReadAllTextAsync(_paths.SettingsFile));
        loaded.Profiles[1].Description = "Changed";
        await _store.SaveAsync(loaded);
        Assert.Equal("Changed", (await _store.LoadAsync()).Profiles[1].Description);
        if (corrupt) Assert.Equal("{ broken", await File.ReadAllTextAsync(path));
        else Assert.False(File.Exists(path));
        Assert.DoesNotContain("SettingsLoadError", await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.DoesNotContain("UnavailableProfileIds", await File.ReadAllTextAsync(_paths.SettingsFile));
        await File.WriteAllTextAsync(path, original);
        var restored = (await _store.LoadAsync()).Profiles[0];
        Assert.Equal("Keep", restored.Name);
        Assert.Empty(restored.SettingsLoadError);
        await _store.UpdateAsync(settings => { settings.IsLogVisible = false; return settings; });
    }

    [Fact]
    public async Task JournalReplayNeverMaterializesUnavailableProfilePlaceholder()
    {
        var missing = new ModProfile { Name = "Missing" };
        var healthy = new ModProfile { Name = "Healthy" };
        await _store.SaveAsync(new AppSettings { Profiles = [missing, healthy] });
        File.Delete(_paths.GetProfileFile(missing.Id));
        var partial = await _store.LoadAsync();
        partial.Profiles[1].Description = "Pending change";
        var journal = JsonSerializer.SerializeToNode(partial)!.AsObject();
        journal["UnavailableProfileIds"] = JsonSerializer.SerializeToNode(new[] { missing.Id });
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, journal.ToJsonString());

        using var restarted = new SettingsStore(_paths);
        var loaded = await restarted.LoadAsync();

        Assert.NotEmpty(loaded.Profiles[0].SettingsLoadError);
        Assert.Equal("Pending change", loaded.Profiles[1].Description);
        Assert.False(File.Exists(_paths.GetProfileFile(missing.Id)));
        Assert.False(File.Exists(_paths.SettingsPendingFile));
    }

    [Fact]
    public async Task ExplicitDeletionOfUnavailableProfileDoesNotRecreateItsJson()
    {
        var missing = new ModProfile { Name = "Missing" };
        var healthy = new ModProfile { Name = "Healthy" };
        await _store.SaveAsync(new AppSettings { Profiles = [missing, healthy] });
        File.Delete(_paths.GetProfileFile(missing.Id));

        await _store.UpdateAsync(settings => { settings.Profiles.RemoveAt(0); return settings; });

        Assert.Equal(healthy.Id, Assert.Single((await _store.LoadAsync()).Profiles).Id);
        Assert.False(File.Exists(_paths.GetProfileFile(missing.Id)));
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken index");
        Assert.Equal(healthy.Id, Assert.Single((await _store.LoadAsync()).Profiles).Id);
    }

    [Theory]
    [InlineData("[\"unknown\"]")]
    [InlineData("[null]")]
    [InlineData("null")]
    public async Task InvalidUnavailableIdsInJournalCannotChangeFiles(string metadata)
    {
        var healthy = new ModProfile { Name = "Healthy" };
        await _store.SaveAsync(new AppSettings { Profiles = [healthy] });
        var original = await File.ReadAllTextAsync(_paths.SettingsFile);
        var journal = JsonSerializer.SerializeToNode(new AppSettings { Profiles = [healthy] })!.AsObject();
        journal["UnavailableProfileIds"] = System.Text.Json.Nodes.JsonNode.Parse(metadata);
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, journal.ToJsonString());

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());

        Assert.Equal(original, await File.ReadAllTextAsync(_paths.SettingsFile));
    }

    [Fact]
    public async Task ImportRejectsUnavailableSourceProfileWithoutChangingDestination()
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "source"), Path.Combine(_root, "workspaces"), false);
        using var source = new SettingsStore(sourcePaths);
        var missing = new ModProfile { Name = "Missing" };
        await source.SaveAsync(new AppSettings { Profiles = [missing] });
        File.Delete(sourcePaths.GetProfileFile(missing.Id));
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "keep" });

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.ImportAsync(sourcePaths.SettingsFile));

        Assert.Equal("keep", (await _store.LoadAsync()).LastBrowsedGamePath);
    }

    [Fact]
    public async Task LockedProfileIsNotReplacedByItsBackup()
    {
        var profile = new ModProfile { Name = "Backup" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        profile.Name = "Current";
        await _store.SaveAsync(settings);
        await using (var locked = new FileStream(_paths.GetProfileFile(profile.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(new AppSettings()));
            Assert.False(Directory.Exists(Path.Combine(_root, "recovery")));
            Assert.True(locked.Length > 0);
        }
        Assert.Equal("Current", Assert.Single((await _store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task RestartCompletesInterruptedSaveWithoutRotatingProfileBackupTwice()
    {
        var first = new ModProfile { Name = "First old" };
        var second = new ModProfile { Name = "Second old" };
        var settings = new AppSettings { Profiles = [first, second], LastBrowsedGamePath = "old" };
        await _store.SaveAsync(settings);
        first.Name = "First new";
        second.Name = "Second new";
        settings.LastBrowsedGamePath = "new";
        using var restarted = new SettingsStore(_paths);
        await using (var locked = new FileStream(_paths.GetProfileFile(second.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(settings));
            Assert.True(File.Exists(_paths.SettingsPendingFile));
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => restarted.LoadAsync());
            Assert.True(locked.Length > 0);
        }

        var loaded = await restarted.LoadAsync();
        Assert.Equal(["First new", "Second new"], loaded.Profiles.Select(profile => profile.Name));
        Assert.Equal("new", loaded.LastBrowsedGamePath);
        Assert.False(File.Exists(_paths.SettingsPendingFile));
        var backup = JsonSerializer.Deserialize<ModProfile>(await File.ReadAllTextAsync(
            Path.ChangeExtension(_paths.GetProfileFile(first.Id), ".backup.json")));
        Assert.Equal("First old", backup!.Name);
        await _store.LoadAsync();
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Profiles\":null}")]
    [InlineData("{\"Profiles\":[]}")]
    [InlineData("{\"ProfileIds\":[]}")]
    [InlineData("{\"SchemaVersion\":10,\"Profiles\":[{}]}")]
    [InlineData("{\"SchemaVersion\":10,\"Profiles\":[],\"Profiles\":[]}")]
    public async Task DamagedJournalBlocksWritesAndIsPreserved(string journal)
    {
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "keep" });
        var original = await File.ReadAllTextAsync(_paths.SettingsFile);
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, journal);
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(new AppSettings()));
        Assert.Equal(original, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(journal, await File.ReadAllTextAsync(_paths.SettingsPendingFile));
    }

    [Fact]
    public async Task ReplayAfterIndexCommitPreservesGeneralSettingsBackup()
    {
        await _store.SaveAsync(new AppSettings { LastBrowsedGamePath = "old" });
        var current = new AppSettings { LastBrowsedGamePath = "new", Profiles = [new ModProfile { Name = "Current" }] };
        await _store.SaveAsync(current);
        var backup = await File.ReadAllTextAsync(_paths.SettingsBackupFile);
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, JsonSerializer.Serialize(current));

        using var restarted = new SettingsStore(_paths);
        Assert.Equal("new", (await restarted.LoadAsync()).LastBrowsedGamePath);
        Assert.Equal(backup, await File.ReadAllTextAsync(_paths.SettingsBackupFile));
        Assert.False(File.Exists(_paths.SettingsPendingFile));
    }

    [Fact]
    public async Task RecoversProfilesWhenBothSettingsFilesAreDamaged()
    {
        var profile = new ModProfile { Name = "Independent profile", LaunchArguments = "-test" };
        await _store.SaveAsync(new AppSettings { Profiles = [profile] });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, "{ broken backup");

        var recovered = await _store.LoadWithRecoveryAsync();

        Assert.Equal(SettingsRecoveryMode.Defaults, recovered.Recovery?.Mode);
        Assert.Equal(2, recovered.Recovery!.Files.Count);
        Assert.Equal("-test", Assert.Single(recovered.Settings.Profiles).LaunchArguments);
        Assert.Equal("Independent profile", Assert.Single((await _store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task DeletionRemovesFilesAndCannotBeUndoneByIndexRecovery()
    {
        var first = new ModProfile { Name = "Removed" };
        var second = new ModProfile { Name = "Kept" };
        await _store.SaveAsync(new AppSettings { Profiles = [first, second] });
        await _store.SaveAsync(new AppSettings { Profiles = [second] });
        Assert.Equal("Kept", Assert.Single((await _store.LoadAsync()).Profiles).Name);
        Assert.False(File.Exists(_paths.GetProfileFile(first.Id)));

        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken index");
        Assert.Equal("Kept", Assert.Single((await _store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task LauncherPreferenceUpdateDoesNotRotateProfileBackup()
    {
        var profile = new ModProfile { Name = "Backup" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        profile.Name = "Current";
        await _store.SaveAsync(settings);
        var backupPath = Path.ChangeExtension(_paths.GetProfileFile(profile.Id), ".backup.json");
        var backup = await File.ReadAllTextAsync(backupPath);
        await _store.UpdateAsync(current => { current.IsLogVisible = false; return current; });
        Assert.Equal(backup, await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    public async Task FailedMigrationKeepsLegacyFileAndResumesAfterRestart()
    {
        Directory.CreateDirectory(_root);
        const string legacy = """{"Profiles":[{"Id":"migration","Name":"Keep me"}]}""";
        await File.WriteAllTextAsync(_paths.SettingsFile, legacy);
        await File.WriteAllTextAsync(_paths.ProfilesDirectory, "directory blocker");

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
        Assert.Equal(legacy, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(legacy, await File.ReadAllTextAsync(Path.Combine(_root, "settings.legacy.json")));
        Assert.True(File.Exists(_paths.SettingsPendingFile));
        File.Delete(_paths.ProfilesDirectory);

        using var restarted = new SettingsStore(_paths);
        Assert.Equal("Keep me", Assert.Single((await restarted.LoadAsync()).Profiles).Name);
        Assert.False(File.Exists(_paths.SettingsPendingFile));
    }

    [Fact]
    public async Task MissingSettingsRecoverProfileFromBackupOnly()
    {
        var profile = new ModProfile { Name = "Backup only" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        profile.Name = "Current";
        await _store.SaveAsync(settings);
        File.Delete(_paths.SettingsFile);
        File.Delete(_paths.SettingsBackupFile);
        File.Delete(_paths.GetProfileFile(profile.Id));

        Assert.True(_store.HasSettingsFile);
        Assert.Equal("Backup only", Assert.Single((await _store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task FutureSchemaBlocksWritesWithoutFallingBackToOlderBackup()
    {
        var profile = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        await _store.SaveAsync(settings);
        var future = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFile))!;
        future["SchemaVersion"] = AppSettings.CurrentSchemaVersion + 1;
        var original = future.ToJsonString();
        await File.WriteAllTextAsync(_paths.SettingsFile, original);

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(new AppSettings()));
        Assert.Equal(original, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.True(File.Exists(_paths.GetProfileFile(profile.Id)));
        Assert.False(Directory.Exists(Path.Combine(_root, "recovery")));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":10,\"UiLanguage\":\"en\"}")]
    [InlineData("{\"SchemaVersion\":10,\"ProfileIds\":[\"same\",\"same\"]}")]
    [InlineData("{\"SchemaVersion\":10,\"ProfileIds\":null}")]
    public async Task InvalidModernIndexRecoversFromBackupInsteadOfDroppingProfiles(string damaged)
    {
        var profile = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        await _store.SaveAsync(settings);
        await File.WriteAllTextAsync(_paths.SettingsFile, damaged);

        var loaded = await _store.LoadWithRecoveryAsync();

        Assert.Equal(SettingsRecoveryMode.Backup, loaded.Recovery?.Mode);
        Assert.Equal(profile.Id, Assert.Single(loaded.Settings.Profiles).Id);
        Assert.Equal(damaged, await File.ReadAllTextAsync(Assert.Single(loaded.Recovery!.Files).RecoveryPath));
    }

    [Fact]
    public async Task UnsafeLegacyIdCannotEscapeProfileDirectory()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(_paths.SettingsFile, """{"Profiles":[{"Id":"../../escape","Name":"Legacy"}]}""");
        var profile = Assert.Single((await _store.LoadAsync()).Profiles);
        Assert.Equal("../../escape", profile.Id);
        Assert.True(File.Exists(_paths.GetProfileFile(profile.Id)));
        Assert.Single(Directory.GetFiles(_paths.ProfilesDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "escape.json")));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
