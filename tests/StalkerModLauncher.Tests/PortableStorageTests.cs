using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using System.Text.Json;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class PortableStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonPortableTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PortableSettingsRebaseInternalPathsAfterMoveAndKeepExternalPathsAbsolute()
    {
        var original = Path.Combine(_root, "original");
        Directory.CreateDirectory(original);
        var paths = new AppPaths(original, useLocalSettings: true);
        paths.EnsureStorageWritable();
        Assert.Equal(Path.Combine(original, "Data", "StalkerModLauncher"), paths.ConfigDirectory);
        Assert.Equal(@"Z:\StalkerModLauncher\Workspaces", paths.GetPreferredWorkspaceRoot(@"Z:\game"));
        Assert.Equal(@"Z:\StalkerModLauncher\Mods", paths.GetDefaultModInstallPath(@"Z:\game"));
        var external = Path.Combine(_root, "external-mod");
        var game = Path.Combine(original, "Games", "game");
        var workspace = paths.GetPreferredWorkspaceRoot(game);
        var profile = new ModProfile
        {
            GameInstallPath = game,
            WorkspacePath = Path.Combine(workspace, "profile-test"),
            ModInstallPath = paths.GetDefaultModInstallPath(game),
            ExecutableSourcePath = external,
            FsgameSourcePath = Path.Combine(external, "fsgame.ltx"),
            UseBaseGameData = true
        };
        profile.Mods.Add(new ModEntry { SourcePath = external });
        var settings = new AppSettings { Profiles = [profile], StartWithWindows = true };
        using (var store = new SettingsStore(paths))
        {
            await store.SaveAsync(settings);
            await store.UpdateAsync(current => current);
        }
        Assert.Equal(Path.Combine(original, "Games", "game"), profile.GameInstallPath);
        var json = await File.ReadAllTextAsync(paths.GetProfileFile(profile.Id));
        Assert.Contains("Games\\\\game", json);
        var moved = Path.Combine(_root, "moved");
        Directory.Move(original, moved);
        var movedPaths = new AppPaths(moved, useLocalSettings: true);
        using var movedStore = new SettingsStore(movedPaths);
        var restored = await movedStore.LoadAsync();
        var restoredProfile = Assert.Single(restored.Profiles);
        Assert.Equal(Path.Combine(moved, "Games", "game"), restoredProfile.GameInstallPath);
        Assert.Equal(Path.Combine(workspace, "profile-test"), restoredProfile.WorkspacePath);
        Assert.Equal(paths.GetDefaultModInstallPath(game), restoredProfile.ModInstallPath);
        Assert.Equal(external, Assert.Single(restoredProfile.Mods).SourcePath);
        Assert.Equal(external, restoredProfile.ExecutableSourcePath);
        Assert.Equal(Path.Combine(external, "fsgame.ltx"), restoredProfile.FsgameSourcePath);
        Assert.True(restoredProfile.UseBaseGameData);
        Assert.True(restored.StartWithWindows);
        Assert.True(File.Exists(movedPaths.SettingsBackupFile));
    }

    [Fact]
    public void RegularExecutableIgnoresPortableFlagAndStandaloneDoesNotFallBack()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), "");
        Assert.False(new AppPaths(_root, useLocalSettings: false).IsPortable);
        File.WriteAllText(Path.Combine(_root, "Data"), "blocks directory creation");
        var paths = new AppPaths(_root, useLocalSettings: true);
        Assert.True(paths.IsPortable);
        Assert.Throws<IOException>(paths.EnsureStorageWritable);
        Assert.Equal(Path.Combine(_root, "Data", "StalkerModLauncher"), paths.ConfigDirectory);
    }

    [Fact]
    public void StandaloneExecutableEnablesLocalSettingsWithoutFlag()
    {
        Assert.True(AppPaths.IsStandaloneExecutable(@"D:\CORDON-Standalone.exe"));
        Assert.True(AppPaths.IsStandaloneExecutable(@"D:\cordon-standalone.EXE"));
        Assert.False(AppPaths.IsStandaloneExecutable(@"D:\CORDON.exe"));
    }

    [Fact]
    public async Task ImportReplacesPortableSettingsAndKeepsBackup()
    {
        var paths = new AppPaths(_root, useLocalSettings: true);
        Directory.CreateDirectory(_root);
        using var store = new SettingsStore(paths);
        await store.SaveAsync(new AppSettings());

        var invalidPath = Path.Combine(_root, "settings.txt");
        await File.WriteAllTextAsync(invalidPath, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(invalidPath));

        var sourcePath = Path.Combine(_root, "imported-settings.json");
        var imported = new AppSettings
        {
            Profiles = [new ModProfile { Name = "Импортированный профиль" }]
        };
        await File.WriteAllTextAsync(sourcePath, JsonSerializer.Serialize(imported));

        await store.ImportAsync(sourcePath);

        var loaded = await store.LoadAsync();
        Assert.Equal("Импортированный профиль", Assert.Single(loaded.Profiles).Name);
        Assert.True(File.Exists(paths.SettingsBackupFile));
    }

    [Fact]
    public async Task FirstPortableLaunchImportsExistingRoamingSettingsOnce()
    {
        var executableDirectory = Path.Combine(_root, "standalone");
        var roamingDirectory = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(roamingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(roamingDirectory, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                Profiles = [new ModProfile { Name = "Профиль из AppData" }]
            }));
        var paths = new AppPaths(executableDirectory, useLocalSettings: true, roamingDirectory);
        using var store = new SettingsStore(paths);

        Assert.True(await store.TryImportRoamingSettingsAsync());
        Assert.Equal("Профиль из AppData", Assert.Single((await store.LoadAsync()).Profiles).Name);

        await File.WriteAllTextAsync(Path.Combine(roamingDirectory, "settings.json"), "{}");
        Assert.False(await store.TryImportRoamingSettingsAsync());
        Assert.Equal("Профиль из AppData", Assert.Single((await store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task ImportsSplitRoamingSettingsWithoutChangingSource()
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "roaming"), Path.Combine(_root, "workspaces"));
        using var source = new SettingsStore(sourcePaths);
        var profile = new ModProfile { Name = "From roaming", GameInstallPath = @"D:\game", TotalPlaytimeSeconds = 123 };
        await source.SaveAsync(new AppSettings { Profiles = [profile] });
        var originalIndex = await File.ReadAllTextAsync(sourcePaths.SettingsFile);
        var originalProfile = await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id));
        var paths = new AppPaths(Path.Combine(_root, "standalone"), useLocalSettings: true, sourcePaths.ConfigDirectory);
        using var destination = new SettingsStore(paths);

        Assert.True(await destination.TryImportRoamingSettingsAsync());
        var loaded = Assert.Single((await destination.LoadAsync()).Profiles);
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.GameInstallPath, loaded.GameInstallPath);
        Assert.Equal(123, loaded.TotalPlaytimeSeconds);
        Assert.Equal(originalIndex, await File.ReadAllTextAsync(sourcePaths.SettingsFile));
        Assert.Equal(originalProfile, await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id)));
    }

    [Fact]
    public async Task PendingLocalSavePreventsAutomaticRoamingImport()
    {
        var roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(roaming);
        await File.WriteAllTextAsync(Path.Combine(roaming, "settings.json"), "{}");
        var paths = new AppPaths(Path.Combine(_root, "standalone"), useLocalSettings: true, roaming);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var profile = new ModProfile { Name = "Pending local" };
        await File.WriteAllTextAsync(paths.SettingsPendingFile, JsonSerializer.Serialize(new AppSettings { Profiles = [profile] }));
        using var store = new SettingsStore(paths);

        Assert.False(await store.TryImportRoamingSettingsAsync());
        Assert.Equal("Pending local", Assert.Single((await store.LoadAsync()).Profiles).Name);
    }

    [Fact]
    public async Task ImportRejectsUnfinishedSourceWithoutChangingEitherStore()
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "source"), Path.Combine(_root, "workspaces"));
        using var source = new SettingsStore(sourcePaths);
        await source.SaveAsync(new AppSettings { Profiles = [new ModProfile { Name = "Source" }] });
        await File.WriteAllTextAsync(sourcePaths.SettingsPendingFile, "{}");
        var sourceIndex = await File.ReadAllTextAsync(sourcePaths.SettingsFile);
        var paths = new AppPaths(Path.Combine(_root, "destination"), useLocalSettings: true);
        using var destination = new SettingsStore(paths);
        var keptGame = Path.Combine(_root, "keep");
        await destination.SaveAsync(new AppSettings { LastBrowsedGamePath = keptGame });

        await Assert.ThrowsAsync<SettingsPersistenceException>(() => destination.ImportAsync(sourcePaths.SettingsFile));

        Assert.Equal(keptGame, (await destination.LoadAsync()).LastBrowsedGamePath);
        Assert.Equal(sourceIndex, await File.ReadAllTextAsync(sourcePaths.SettingsFile));
        Assert.Equal("{}", await File.ReadAllTextAsync(sourcePaths.SettingsPendingFile));
    }

    [Fact]
    public async Task ImportsSplitPortableSettingsRelativeToSourceExecutable()
    {
        var sourcePaths = new AppPaths(Path.Combine(_root, "source"), useLocalSettings: true);
        using var source = new SettingsStore(sourcePaths);
        var profile = new ModProfile { Name = "Portable", GameInstallPath = Path.Combine(sourcePaths.ExecutableDirectory, "game") };
        await source.SaveAsync(new AppSettings { Profiles = [profile] });
        var originalProfile = await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id));
        var paths = new AppPaths(Path.Combine(_root, "destination"), useLocalSettings: true);
        using var destination = new SettingsStore(paths);

        await destination.ImportAsync(sourcePaths.SettingsFile);

        Assert.Equal(profile.GameInstallPath, Assert.Single((await destination.LoadAsync()).Profiles).GameInstallPath);
        Assert.Equal(originalProfile, await File.ReadAllTextAsync(sourcePaths.GetProfileFile(profile.Id)));
    }

    [Fact]
    public void PreventsScanningOwnDataAndAllowsManagedMods()
    {
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root, useLocalSettings: true);
        Assert.Throws<InvalidOperationException>(() => paths.ValidateSourceDirectory(_root));
        Assert.Throws<InvalidOperationException>(() => paths.ValidateSourceDirectory(Path.Combine(paths.ConfigDirectory, "nested-mod")));
        Assert.Throws<InvalidOperationException>(() => paths.ValidateSourceDirectory(Path.Combine(paths.WorkspaceRoot, "profile-test")));
        paths.ValidateSourceDirectory(Path.Combine(paths.GetDefaultModInstallPath(null), "mod"));
        Assert.Throws<InvalidDataException>(() => paths.FromStoredPath(@"..\outside"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
