using System.Text.Json;
using System.Text.Json.Nodes;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileDeletionPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonProfileDeletionTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;

    public ProfileDeletionPersistenceTests()
    {
        _paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        _store = new SettingsStore(_paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletesOnlyRemovedProfilesAndTheirBackups(bool removeLast)
    {
        var removed = new ModProfile { Name = "Removed" };
        var kept = new ModProfile { Name = "Kept" };
        var settings = new AppSettings { Profiles = [removed, kept] };
        await _store.SaveAsync(settings);
        removed.Name = "Updated";
        await _store.SaveAsync(settings);
        var keptJson = await File.ReadAllTextAsync(_paths.GetProfileFile(kept.Id));
        var orphanPath = Path.Combine(_paths.ProfilesDirectory, "profile-unrelated.json");
        await File.WriteAllTextAsync(orphanPath, "unrelated file");

        settings.Profiles = removeLast ? [] : [kept];
        await _store.SaveAsync(settings);

        Assert.False(File.Exists(_paths.GetProfileFile(removed.Id)));
        Assert.False(File.Exists(Path.ChangeExtension(_paths.GetProfileFile(removed.Id), ".backup.json")));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(orphanPath));
        if (!removeLast) Assert.Equal(keptJson, await File.ReadAllTextAsync(_paths.GetProfileFile(kept.Id)));
        foreach (var path in new[] { _paths.SettingsFile, _paths.SettingsBackupFile })
        {
            using var index = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.DoesNotContain(removed.Id, index.RootElement.GetProperty("ProfileIds").EnumerateArray().Select(id => id.GetString()));
            Assert.False(index.RootElement.TryGetProperty("DeletedProfileIds", out _));
        }
    }

    [Fact]
    public async Task DeletionUsesBackupIndexWhenPrimaryIsMissing()
    {
        var profile = new ModProfile { Name = "Removed" };
        var settings = new AppSettings { Profiles = [profile] };
        await _store.SaveAsync(settings);
        await _store.SaveAsync(settings);
        File.Delete(_paths.SettingsFile);

        await _store.SaveAsync(new AppSettings());

        Assert.False(File.Exists(_paths.GetProfileFile(profile.Id)));
        Assert.Empty((await _store.LoadAsync()).Profiles);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        Assert.Empty((await _store.LoadAsync()).Profiles);
    }

    [Fact]
    public async Task DeletingLegacyUnsafeIdNeverTouchesFilesOutsideProfileDirectory()
    {
        var profile = new ModProfile { Id = "../../escape", Name = "Removed" };
        var outside = Path.Combine(_root, "escape.json");
        await _store.SaveAsync(new AppSettings { Profiles = [profile] });
        await File.WriteAllTextAsync(outside, "keep");

        await _store.SaveAsync(new AppSettings());

        Assert.False(File.Exists(_paths.GetProfileFile(profile.Id)));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task DeletedProfileDoesNotReturnAfterLosingBothSettingsFiles()
    {
        var removed = new ModProfile { Name = "Removed" };
        var kept = new ModProfile { Name = "Kept" };
        await _store.SaveAsync(new AppSettings { Profiles = [removed, kept] });
        await _store.UpdateAsync(settings => { settings.Profiles.RemoveAt(0); return settings; });
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken primary");
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, "{ broken backup");

        Assert.Equal(kept.Id, Assert.Single((await _store.LoadAsync()).Profiles).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedDeletionIsCompletedOnReload(bool lockBackup)
    {
        var removed = new ModProfile { Name = "Removed" };
        var kept = new ModProfile { Name = "Kept" };
        var settings = new AppSettings { Profiles = [removed, kept] };
        await _store.SaveAsync(settings);
        removed.Name = "Updated";
        await _store.SaveAsync(settings);
        var profilePath = _paths.GetProfileFile(removed.Id);
        var backupPath = Path.ChangeExtension(profilePath, ".backup.json");
        var lockedPath = lockBackup ? backupPath : profilePath;
        settings.Profiles = [kept];

        await using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(settings));
            Assert.True(File.Exists(_paths.SettingsPendingFile));
            await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.SaveAsync(settings));
            Assert.True(locked.Length > 0);
        }

        using var restarted = new SettingsStore(_paths);
        Assert.Equal(kept.Id, Assert.Single((await restarted.LoadAsync()).Profiles).Id);
        Assert.False(File.Exists(profilePath));
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(_paths.SettingsPendingFile));
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken index");
        Assert.Equal(kept.Id, Assert.Single((await restarted.LoadAsync()).Profiles).Id);
    }

    [Fact]
    public async Task ReplayFiltersLegacyBackupBeforeDeletingProfileFiles()
    {
        var removed = new ModProfile { Name = "Removed" };
        var kept = new ModProfile { Name = "Kept" };
        await _store.SaveAsync(new AppSettings { Profiles = [removed, kept] });
        await _store.SaveAsync(new AppSettings { Profiles = [kept] });
        await File.WriteAllTextAsync(_paths.GetProfileFile(removed.Id), JsonSerializer.Serialize(removed));
        var previous = new AppSettings { SchemaVersion = 9, Profiles = [removed, kept], LastBrowsedGamePath = "previous" };
        await File.WriteAllTextAsync(_paths.SettingsBackupFile, JsonSerializer.Serialize(previous));
        var pending = JsonSerializer.SerializeToNode(new AppSettings { Profiles = [kept] })!.AsObject();
        pending["DeletedProfileIds"] = JsonSerializer.SerializeToNode(new[] { removed.Id });
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, pending.ToJsonString());

        Assert.Equal(kept.Id, Assert.Single((await _store.LoadAsync()).Profiles).Id);
        Assert.False(File.Exists(_paths.GetProfileFile(removed.Id)));
        var backup = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_paths.SettingsBackupFile));
        Assert.Equal(kept.Id, Assert.Single(backup!.ProfileIds!));
        Assert.Equal("previous", backup.LastBrowsedGamePath);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    public async Task InvalidDeletionJournalCannotChangeFiles(string deletionJson)
    {
        var kept = new ModProfile { Name = "Kept" };
        await _store.SaveAsync(new AppSettings { Profiles = [kept] });
        var index = await File.ReadAllTextAsync(_paths.SettingsFile);
        var profile = await File.ReadAllTextAsync(_paths.GetProfileFile(kept.Id));
        var pending = JsonSerializer.SerializeToNode(new AppSettings { Profiles = [kept] })!.AsObject();
        pending["DeletedProfileIds"] = JsonNode.Parse(deletionJson);
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, pending.ToJsonString());

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
        Assert.Equal(index, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(profile, await File.ReadAllTextAsync(_paths.GetProfileFile(kept.Id)));
    }

    [Fact]
    public async Task JournalCannotDeleteAnActiveProfile()
    {
        var kept = new ModProfile { Name = "Kept" };
        await _store.SaveAsync(new AppSettings { Profiles = [kept] });
        var index = await File.ReadAllTextAsync(_paths.SettingsFile);
        var pending = JsonSerializer.SerializeToNode(new AppSettings { Profiles = [kept] })!.AsObject();
        pending["DeletedProfileIds"] = JsonSerializer.SerializeToNode(new[] { kept.Id });
        await File.WriteAllTextAsync(_paths.SettingsPendingFile, pending.ToJsonString());

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => _store.LoadAsync());
        Assert.Equal(index, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.True(File.Exists(_paths.GetProfileFile(kept.Id)));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
