using System.Text.Json;
using System.Text.Json.Nodes;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class SettingsDamageRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonSettingsDamageTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;

    public SettingsDamageRecoveryTests()
    {
        _paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        _store = new SettingsStore(_paths);
    }

    [Fact]
    public async Task EmptyObjectRestoresBackupWithoutErasingProfileIndex()
    {
        var profile = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        await _store.SaveAsync(settings);
        var backup = await File.ReadAllTextAsync(_paths.SettingsBackupFile);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{}");

        var loaded = await _store.LoadWithRecoveryAsync();

        Assert.Equal(profile.Id, Assert.Single(loaded.Settings.Profiles).Id);
        Assert.Equal(SettingsRecoveryMode.Backup, loaded.Recovery?.Mode);
        Assert.Equal(backup, await File.ReadAllTextAsync(_paths.SettingsBackupFile));
        Assert.Equal("{}", await File.ReadAllTextAsync(Assert.Single(loaded.Recovery!.Files).RecoveryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingIndexesAndOneDamagedProfileDoNotBlockHealthyProfile(bool damageProfileBackup)
    {
        var bad = new ModProfile { Name = "Broken" };
        var healthy = new ModProfile { Name = "Healthy" };
        var settings = new AppSettings { Profiles = [bad, healthy] };
        await _store.SaveAsync(settings);
        if (damageProfileBackup)
        {
            bad.Description = "Changed";
            await _store.SaveAsync(settings);
        }
        await File.WriteAllTextAsync(_paths.SettingsFile, "{broken primary");
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, "{broken backup");
        var badPath = _paths.GetProfileFile(bad.Id);
        await File.WriteAllTextAsync(badPath, "{broken profile");
        if (damageProfileBackup) await File.WriteAllTextAsync(Path.ChangeExtension(badPath, ".backup.json"), "{broken profile backup");

        var loaded = await _store.LoadWithRecoveryAsync();

        Assert.Contains(loaded.Settings.Profiles, profile => profile.Id == healthy.Id && profile.SettingsLoadError.Length == 0);
        Assert.Equal("{broken profile", await File.ReadAllTextAsync(badPath));
        Assert.Contains(loaded.Recovery!.Files, file => file.OriginalPath == badPath);
        Assert.Contains((await _store.LoadAsync()).Profiles, profile => profile.Id == healthy.Id);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("1e20")]
    public async Task InvalidLegacyPlaytimeUsesSettingsBackup(string number)
    {
        var healthy = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [healthy] };
        await _store.SaveAsync(settings);
        await _store.SaveAsync(settings);
        await File.WriteAllTextAsync(_paths.SettingsFile,
            "{\"SchemaVersion\":9,\"Profiles\":[{\"Id\":\"bad\",\"TotalPlaytimeSeconds\":" + number + "}]}");

        var loaded = await _store.LoadWithRecoveryAsync();

        Assert.Equal(healthy.Id, Assert.Single(loaded.Settings.Profiles).Id);
        Assert.Equal(SettingsRecoveryMode.Backup, loaded.Recovery?.Mode);
    }

    [Theory]
    [InlineData("LastBrowsedGamePath")]
    [InlineData("LastMo2SourcePath")]
    public async Task InvalidPortableGeneralPathUsesSettingsBackup(string property)
    {
        var paths = new AppPaths(Path.Combine(_root, "portable"), useLocalSettings: true);
        using var store = new SettingsStore(paths);
        var profile = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [profile] };
        await store.SaveAsync(settings);
        await store.SaveAsync(settings);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!;
        document[property] = @"..\outside";
        await File.WriteAllTextAsync(paths.SettingsFile, document.ToJsonString());

        var loaded = await store.LoadWithRecoveryAsync();

        Assert.Equal(profile.Id, Assert.Single(loaded.Settings.Profiles).Id);
        Assert.Equal(SettingsRecoveryMode.Backup, loaded.Recovery?.Mode);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("1e20")]
    [InlineData("-1")]
    public async Task InvalidPlaytimeInJournalIsRejectedBeforeChangingFiles(string number)
    {
        var profile = new ModProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        var index = await File.ReadAllTextAsync(_paths.SettingsFile);
        var profileJson = await File.ReadAllTextAsync(_paths.GetProfileFile(profile.Id));
        var journal = JsonSerializer.SerializeToNode(settings)!;
        journal["Profiles"]![0]!["TotalPlaytimeSeconds"] = JsonNode.Parse(number);
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, journal.ToJsonString());

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());

        Assert.Equal(index, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(profileJson, await File.ReadAllTextAsync(_paths.GetProfileFile(profile.Id)));
        Assert.True(File.Exists(_paths.SettingsPendingFile));
    }

    [Theory]
    [InlineData("GameInstallPath")]
    [InlineData("ExecutableSourcePath")]
    [InlineData("FsgameSourcePath")]
    [InlineData("WorkspacePath")]
    [InlineData("ModInstallPath")]
    [InlineData("Mo2OverwritePath")]
    [InlineData("ModSourcePath")]
    public async Task InvalidPortableProfilePathFallsBackToItsBackup(string property)
    {
        var paths = new AppPaths(Path.Combine(_root, "portable"), useLocalSettings: true);
        using var store = new SettingsStore(paths);
        var profile = new ModProfile { Name = "Original", GameInstallPath = Path.Combine(paths.ExecutableDirectory, "game") };
        profile.Mods.Add(new ModEntry { SourcePath = Path.Combine(paths.ExecutableDirectory, "mod"), Order = 1 });
        var healthy = new ModProfile { Name = "Healthy" };
        var settings = new AppSettings { Profiles = [profile, healthy] };
        await store.SaveAsync(settings);
        profile.Description = "Changed";
        await store.SaveAsync(settings);
        var path = paths.GetProfileFile(profile.Id);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        if (property == "ModSourcePath") document["Mods"]![0]!["SourcePath"] = @"..\outside";
        else document[property] = @"..\outside";
        var damaged = document.ToJsonString();
        await File.WriteAllTextAsync(path, damaged);

        var loaded = await store.LoadWithRecoveryAsync();

        Assert.Equal(2, loaded.Settings.Profiles.Count);
        Assert.All(loaded.Settings.Profiles, item => Assert.Empty(item.SettingsLoadError));
        Assert.Equal(profile.GameInstallPath, loaded.Settings.Profiles[0].GameInstallPath);
        Assert.Equal(string.Empty, loaded.Settings.Profiles[0].Description);
        Assert.Equal(damaged, await File.ReadAllTextAsync(Assert.Single(loaded.Recovery!.Files).RecoveryPath));
        var restoredJson = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal("game", restoredJson["GameInstallPath"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("1e999", true)]
    [InlineData("1e999", false)]
    [InlineData("1e20", true)]
    [InlineData("1e20", false)]
    [InlineData("-1", true)]
    [InlineData("-1", false)]
    public async Task InvalidPlaytimeDoesNotBreakOtherProfilesOrSaving(string number, bool backup)
    {
        var profile = new ModProfile { Name = "Original", TotalPlaytimeSeconds = 20 };
        var healthy = new ModProfile { Name = "Healthy" };
        var settings = new AppSettings { Profiles = [profile, healthy] };
        await _store.SaveAsync(settings);
        if (backup)
        {
            profile.Description = "Changed";
            await _store.SaveAsync(settings);
        }
        var path = _paths.GetProfileFile(profile.Id);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["TotalPlaytimeSeconds"] = JsonNode.Parse(number);
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var loaded = await _store.LoadAsync();

        Assert.Empty(loaded.Profiles[1].SettingsLoadError);
        if (backup) Assert.Equal(20, loaded.Profiles[0].TotalPlaytimeSeconds);
        else Assert.NotEmpty(loaded.Profiles[0].SettingsLoadError);
        Assert.False(string.IsNullOrEmpty(loaded.Profiles[0].PlaytimeDisplay));
        await _store.SaveAsync(loaded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollidingFileNamesAreRejectedBeforeWritingJournal(bool legacy)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 9,
            Profiles = [new ModProfile { Id = "custom profile id" }, new ModProfile { Id = "4f324341a1f26218" }]
        };
        var original = JsonSerializer.Serialize(settings);
        if (legacy)
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(_paths.SettingsFile, original);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (legacy) await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
            else await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(settings));
            Assert.False(File.Exists(_paths.SettingsPendingFile));
        }
        if (legacy) Assert.Equal(original, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.False(Directory.Exists(_paths.ProfilesDirectory));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
