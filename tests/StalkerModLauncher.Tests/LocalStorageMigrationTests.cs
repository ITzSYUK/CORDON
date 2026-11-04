using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using System.Text.Json;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LocalStorageMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonMigrationTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportedExternalWorkspaceKeepsSavesAndSettingsDuringLaunchPreparation()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | $fs_root$ | appdata");
        var profile = new ModProfile { GameInstallPath = game };
        var workspace = Path.Combine(_root, "StalkerModLauncher", "Workspace", $"profile-{profile.Id}");
        profile.WorkspacePath = workspace;
        Directory.CreateDirectory(Path.Combine(workspace, "userdata", "savedgames"));
        Directory.CreateDirectory(Path.Combine(workspace, "userdata", "writable-game-files"));
        File.WriteAllText(Path.Combine(workspace, "userdata", "savedgames", "old.sav"), "old save");
        File.WriteAllText(Path.Combine(workspace, "userdata", "user.ltx"), "old user settings");
        File.WriteAllText(Path.Combine(workspace, "userdata", "writable-game-files", "commandline.txt"), "-old-option");
        var sourcePaths = new AppPaths(Path.Combine(_root, "old-config"), Path.Combine(_root, "unused"), false);
        using (var source = new SettingsStore(sourcePaths)) await source.SaveAsync(new AppSettings { Profiles = [profile] });
        var sourceBytes = File.ReadAllBytes(sourcePaths.GetProfileFile(profile.Id));
        var paths = new AppPaths(Path.Combine(_root, "launcher"), useLocalSettings: true,
            sourcePaths.ConfigDirectory, Path.Combine(_root, "empty-source"));
        using var destination = new SettingsStore(paths);

        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());
        var settings = await destination.LoadAsync();
        var imported = Assert.Single(settings.Profiles);
        var builder = new WorkspaceBuilder(paths);
        Assert.Equal(workspace, new ProfileManager(paths, builder).EnsureProfileFolderPath(imported));
        await ProfileDataTestFixture.PrepareAsync(builder, game, imported);
        await destination.SaveAsync(settings);

        Assert.Equal(workspace, Assert.Single((await destination.LoadAsync()).Profiles).WorkspacePath);
        Assert.Equal("old save", File.ReadAllText(Path.Combine(workspace, "userdata", "savedgames", "old.sav")));
        Assert.Equal("old user settings", File.ReadAllText(Path.Combine(workspace, "userdata", "user.ltx")));
        Assert.Equal("-old-option", File.ReadAllText(Path.Combine(workspace, "userdata", "writable-game-files", "commandline.txt")));
        Assert.Contains(Path.Combine(workspace, "userdata"), File.ReadAllText(Path.Combine(workspace, "userdata", "overwrite", "fsgame.ltx")));
        Assert.False(Directory.Exists(paths.WorkspaceRoot));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePaths.GetProfileFile(profile.Id)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImportRecoversProfileFilesWithoutIndexesAndNeverChangesSource(bool corruptIndexes, bool backupOnly)
    {
        var local = Path.Combine(_root, "Data", "StalkerModLauncher");
        var sourcePaths = new AppPaths(local, Path.Combine(local, "Workspaces"), _root);
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game"), WorkspacePath = Path.Combine(local, "Workspaces", "profile-test") };
        using (var source = new SettingsStore(sourcePaths))
            await source.SaveAsync(new AppSettings { Profiles = [profile] });
        if (corruptIndexes)
        {
            File.WriteAllText(sourcePaths.SettingsFile, "{broken");
            File.WriteAllText(sourcePaths.SettingsBackupFile, "{broken");
        }
        else File.Delete(sourcePaths.SettingsFile);
        if (backupOnly)
        {
            var file = sourcePaths.GetProfileFile(profile.Id);
            File.Copy(file, Path.ChangeExtension(file, ".backup.json"));
            File.Delete(file);
        }
        var originals = Directory.GetFiles(local, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        using var destination = new SettingsStore(paths);

        Assert.Contains(local, destination.GetProfileMigrationSources());
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());

        var imported = Assert.Single((await destination.LoadAsync()).Profiles);
        Assert.Equal(profile.Id, imported.Id);
        Assert.Equal(profile.GameInstallPath, imported.GameInstallPath);
        Assert.Equal(profile.WorkspacePath, imported.WorkspacePath);
        Assert.Equal(originals.Count, Directory.GetFiles(local, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ImportRejectsDamagedOrphanProfileWithoutChangingEitherStore()
    {
        var local = Path.Combine(_root, "Data", "StalkerModLauncher", "profiles");
        Directory.CreateDirectory(local);
        var file = Path.Combine(local, "profile-test.json");
        File.WriteAllText(file, "{broken");
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        using var destination = new SettingsStore(paths);
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.TransferDataAsync(new Progress<(double Fraction, string Path)>()));
        Assert.False(destination.HasSettingsFile);
        Assert.Equal("{broken", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(local));
    }

    [Fact]
    public async Task OldFullTransferMarkersCannotCopyFilesOrRebasePathsOnLoadOrSave()
    {
        var local = Path.Combine(_root, "Data", "StalkerModLauncher");
        var mod = Path.Combine(local, "Mods", "example");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "payload"), "mod");
        var profile = new ModProfile { ModInstallPath = Path.Combine(local, "Mods"), WorkspacePath = Path.Combine(local, "Workspaces", "profile-test") };
        profile.Mods.Add(new ModEntry { SourcePath = mod });
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        using var store = new SettingsStore(paths);
        await store.SaveAsync(new AppSettings { Profiles = [profile] });
        paths.SetMigrationDecision(true);
        File.WriteAllText(Path.Combine(_root, "Data", ".cordon-data-migration"), local);

        Assert.False(await store.TryImportRoamingSettingsAsync());
        await store.LoadAsync();
        await store.UpdateAsync(settings => settings);
        var loaded = Assert.Single((await store.LoadAsync()).Profiles);

        Assert.Equal(mod, loaded.Mods[0].SourcePath);
        Assert.Equal(profile.WorkspacePath, loaded.WorkspacePath);
        Assert.False(Directory.Exists(Path.Combine(paths.ConfigDirectory, "Mods")));
        Assert.False(Directory.Exists(paths.WorkspaceRoot));
        Assert.Equal("mod", File.ReadAllText(Path.Combine(mod, "payload")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProfilesOnlyWizardImportsOldLocalProfilesWithoutCopyingData(bool hasDestination, bool splitProfiles)
    {
        var local = Path.Combine(_root, "Data", "StalkerModLauncher");
        var profile = new ModProfile
        {
            Name = "Old local profile",
            WorkspacePath = Path.Combine(local, "Workspaces", "profile-test"),
            ModInstallPath = Path.Combine(local, "Mods")
        };
        profile.Mods.Add(new ModEntry { SourcePath = Path.Combine(local, "Mods", "example") });
        var sourcePaths = new AppPaths(local, Path.Combine(local, "Workspaces"), _root);
        var settings = new AppSettings { UiLanguage = "en", Profiles = [profile] };
        Directory.CreateDirectory(local);
        if (splitProfiles)
        {
            using var source = new SettingsStore(sourcePaths);
            await source.SaveAsync(settings);
        }
        else File.WriteAllText(sourcePaths.SettingsFile, JsonSerializer.Serialize(settings));
        foreach (var relative in new[] { @"Workspaces\profile-test\userdata\save", @"Mods\example\mod.ltx", @"Cache\payload", @"Temp\payload", "launcher.log" })
        {
            var path = Path.Combine(local, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }
        var originals = Directory.GetFiles(local, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        paths.EnsureStorageWritable();
        paths.SetMigrationDecision(false);
        using var destination = new SettingsStore(paths);
        var existing = new ModProfile { Name = "Existing destination profile" };
        if (hasDestination) await destination.SaveAsync(new AppSettings { Profiles = [existing] });

        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());

        var loaded = await destination.LoadAsync();
        Assert.Equal(hasDestination ? 2 : 1, loaded.Profiles.Count);
        var imported = Assert.Single(loaded.Profiles, item => item.Id == profile.Id);
        Assert.Equal(profile.WorkspacePath, imported.WorkspacePath);
        Assert.Equal(profile.ModInstallPath, imported.ModInstallPath);
        Assert.Equal(profile.Mods[0].SourcePath, imported.Mods[0].SourcePath);
        Assert.NotEqual("en", loaded.UiLanguage);
        Assert.False(paths.MigrationDecision);
        Assert.False(Directory.Exists(paths.WorkspaceRoot));
        Assert.False(Directory.Exists(Path.Combine(paths.ConfigDirectory, "Mods")));
        Assert.False(File.Exists(Path.Combine(paths.CacheDirectory, "payload")));
        Assert.False(File.Exists(Path.Combine(paths.TempDirectory, "payload")));
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "launcher.log")));
        Assert.Equal(originals.Count, Directory.GetFiles(local, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WizardUsesHealthyAppDataBackupWithoutChangingSource(bool hasDestination)
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "roaming"), Path.Combine(_root, "unused"));
        var profile = new ModProfile { Name = "Recovered from backup" };
        using (var source = new SettingsStore(sourcePaths))
        {
            var settings = new AppSettings { UiLanguage = "en", Profiles = [profile] };
            await source.SaveAsync(settings);
            await source.SaveAsync(settings);
        }
        File.WriteAllText(sourcePaths.SettingsFile, "{broken");
        var originals = Directory.GetFiles(sourcePaths.ConfigDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var paths = new AppPaths(_root, useLocalSettings: true, sourcePaths.ConfigDirectory, Path.Combine(_root, "legacy"));
        using var destination = new SettingsStore(paths);
        if (hasDestination) await destination.SaveAsync(new AppSettings());

        Assert.Contains(sourcePaths.ConfigDirectory, destination.GetProfileMigrationSources());
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());

        var loaded = await destination.LoadAsync();
        Assert.Equal(profile.Id, Assert.Single(loaded.Profiles).Id);
        Assert.NotEqual("en", loaded.UiLanguage);
        Assert.Equal(originals.Count, Directory.GetFiles(sourcePaths.ConfigDirectory, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("locked")]
    [InlineData("pending")]
    public async Task WizardDoesNotUseBackupToBypassUnavailableSource(string failure)
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "roaming"), Path.Combine(_root, "unused"));
        using (var source = new SettingsStore(sourcePaths))
        {
            await source.SaveAsync(new AppSettings());
            await source.SaveAsync(new AppSettings());
        }
        if (failure == "unsupported")
            File.WriteAllText(sourcePaths.SettingsFile, $"{{\"SchemaVersion\":{AppSettings.CurrentSchemaVersion + 1},\"ProfileIds\":[]}}");
        if (failure == "pending") File.WriteAllText(sourcePaths.SettingsPendingFile, "{broken");
        using var locked = failure == "locked" ? new FileStream(sourcePaths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var backup = File.ReadAllBytes(sourcePaths.SettingsBackupFile);
        var paths = new AppPaths(_root, useLocalSettings: true, sourcePaths.ConfigDirectory, Path.Combine(_root, "legacy"));
        paths.EnsureStorageWritable();
        using var destination = new SettingsStore(paths);

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.TransferDataAsync(new Progress<(double Fraction, string Path)>()));

        Assert.False(destination.HasSettingsFile);
        Assert.Null(paths.MigrationDecision);
        Assert.Equal(backup, File.ReadAllBytes(sourcePaths.SettingsBackupFile));
    }

    [Fact]
    public async Task StartupLoggingBeforeWizardDoesNotConflictWithOldLocalLogs()
    {
        var old = Path.Combine(_root, "Data", "StalkerModLauncher");
        using (var source = new SettingsStore(new AppPaths(old, Path.Combine(old, "Workspaces"))))
            await source.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Original" }] });
        File.WriteAllText(Path.Combine(old, "launcher.log"), "old log");
        File.WriteAllText(Path.Combine(old, "launcher.old.log"), "older log");
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        paths.EnsureStorageWritable();
        using var destination = new SettingsStore(paths);
        Assert.Empty((await destination.LoadAsync()).Profiles);
        var logger = new ApplicationLogService(paths);
        logger.Write(new string('x', 1024 * 1024));
        logger.Write("startup before wizard");

        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());

        Assert.Equal("Original", Assert.Single((await destination.LoadAsync()).Profiles).Name);
        Assert.Contains("startup before wizard", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "launcher.log")));
        Assert.True(File.Exists(Path.Combine(paths.ConfigDirectory, "launcher.old.log")));
        Assert.Equal("old log", File.ReadAllText(Path.Combine(old, "launcher.log")));
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "launcher.migration.log")));
        Assert.False(File.Exists(Path.Combine(_root, "Data", ".cordon-data-migration")));
        logger.Write("after migration");
        Assert.Contains("after migration", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "launcher.log")));
    }

    [Fact]
    public async Task ProfilesOnlyWizardImportsAppDataWithoutMovingReferencedStorage()
    {
        var roaming = Path.Combine(_root, "roaming");
        var sourcePaths = new AppPaths(roaming, Path.Combine(_root, "unused"));
        var drive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => $"{(char)value}:\\")
            .First(root => !Directory.GetLogicalDrives().Contains(root, StringComparer.OrdinalIgnoreCase));
        var profile = new ModProfile { Name = "Only metadata", WorkspacePath = Path.Combine(drive, "StalkerModLauncher", "Workspaces", "profile-test") };
        profile.Mods.Add(new ModEntry { SourcePath = Path.Combine(drive, "StalkerModLauncher", "Mods", "example") });
        using (var source = new SettingsStore(sourcePaths)) await source.SaveAsync(new AppSettings { Profiles = [profile] });
        var original = File.ReadAllBytes(sourcePaths.GetProfileFile(profile.Id));
        var paths = new AppPaths(_root, useLocalSettings: true, roaming, Path.Combine(_root, "legacy"));
        paths.SetMigrationDecision(true);
        using var destination = new SettingsStore(paths);
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());
        Assert.False(paths.MigrationDecision);
        var restored = Assert.Single((await destination.LoadAsync()).Profiles);
        Assert.Equal(profile.WorkspacePath, restored.WorkspacePath);
        Assert.Equal(profile.Mods[0].SourcePath, restored.Mods[0].SourcePath);
        await destination.UpdateAsync(settings => settings);
        Assert.Equal(profile.WorkspacePath, Assert.Single((await destination.LoadAsync()).Profiles).WorkspacePath);
        Assert.False(Directory.Exists(Path.Combine(paths.ConfigDirectory, "Mods")));
        Assert.Equal(original, File.ReadAllBytes(sourcePaths.GetProfileFile(profile.Id)));
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());
        Assert.Single((await destination.LoadAsync()).Profiles);
    }


    [Fact]
    public async Task ProfilesOnlyWizardMergesAllStoresWithAppDataPriority()
    {
        var current = Path.Combine(_root, "roaming");
        var legacy = Path.Combine(_root, "legacy");
        var profile = new ModProfile { Name = "Current" };
        var old = new ModProfile { Name = "Legacy only" };
        var local = new ModProfile { Name = "Local only" };
        using (var source = new SettingsStore(new AppPaths(current, Path.Combine(_root, "unused"))))
            await source.SaveAsync(new AppSettings { Profiles = [profile] });
        using (var source = new SettingsStore(new AppPaths(legacy, Path.Combine(_root, "unused"))))
            await source.SaveAsync(new AppSettings { Profiles = [new ModProfile { Id = profile.Id, Name = "Old duplicate" }, old] });
        var localRoot = Path.Combine(_root, "Data", "StalkerModLauncher");
        using (var source = new SettingsStore(new AppPaths(localRoot, Path.Combine(localRoot, "Workspaces"))))
            await source.SaveAsync(new AppSettings { Profiles = [new ModProfile { Id = profile.Id, Name = "Local duplicate" }, local] });
        var paths = new AppPaths(_root, useLocalSettings: true, current, legacy);
        using var destination = new SettingsStore(paths);
        await destination.TransferDataAsync(new Progress<(double Fraction, string Path)>());
        var profiles = (await destination.LoadAsync()).Profiles;
        Assert.Equal(3, profiles.Count);
        Assert.Equal("Current", profiles.Single(item => item.Id == profile.Id).Name);
        Assert.Contains(profiles, item => item.Id == old.Id);
        Assert.Contains(profiles, item => item.Id == local.Id);
    }


    [Fact]
    public async Task PreviewListsOldLocalStoreWithoutCopyingData()
    {
        var source = Path.Combine(_root, "Data", "StalkerModLauncher");
        using (var original = new SettingsStore(new AppPaths(source, Path.Combine(source, "Workspaces"))))
            await original.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Preview only" }] });
        var before = File.ReadAllBytes(Path.Combine(source, "settings.json"));
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        using var store = new SettingsStore(paths);
        Assert.Equal([source], store.GetProfileMigrationSources());
        Assert.False(Directory.Exists(paths.ConfigDirectory));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(source, "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task DeclineOrMissingChoiceNeverImportsOldData(bool? decision)
    {
        var source = Path.Combine(_root, "Data", "StalkerModLauncher");
        using (var original = new SettingsStore(new AppPaths(source, Path.Combine(source, "Workspaces"))))
            await original.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Must stay at source" }] });
        var paths = new AppPaths(_root, useLocalSettings: true, Path.Combine(_root, "roaming"), Path.Combine(_root, "legacy"));
        if (decision is { } accepted) paths.SetMigrationDecision(accepted);
        using var store = new SettingsStore(paths);
        Assert.False(await store.TryImportRoamingSettingsAsync());
        Assert.Empty((await store.LoadAsync()).Profiles);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(Path.Combine(source, "settings.json")));
        Assert.Equal(decision, new AppPaths(_root).MigrationDecision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

}
