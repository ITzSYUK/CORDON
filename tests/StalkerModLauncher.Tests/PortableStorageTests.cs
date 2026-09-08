using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
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
        var json = await File.ReadAllTextAsync(paths.SettingsFile);
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
        Assert.True(restoredProfile.UseBaseGameData);
        Assert.False(restored.StartWithWindows);
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
    public void PreventsScanningOwnDataAndAllowsManagedMods()
    {
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root, useLocalSettings: true);
        Assert.Throws<InvalidOperationException>(() => paths.ValidateSourceDirectory(_root));
        Assert.Throws<InvalidOperationException>(() => paths.ValidateSourceDirectory(Path.Combine(paths.WorkspaceRoot, "profile-test")));
        paths.ValidateSourceDirectory(Path.Combine(paths.GetDefaultModInstallPath(null), "mod"));
        Assert.Throws<InvalidDataException>(() => paths.FromStoredPath(@"..\outside"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
