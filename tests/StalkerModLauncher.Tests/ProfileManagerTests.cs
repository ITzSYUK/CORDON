using System.Security.Cryptography;
using System.Text;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileManagerTests
{
    private readonly FakeWorkspaceManager _workspaceManager = new();
    private readonly AppPaths _paths;
    private readonly ProfileManager _manager;

    public ProfileManagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(root, Path.Combine(root, "workspaces"), false);
        _manager = new ProfileManager(_paths, _workspaceManager);
    }

    [Fact]
    public async Task DeleteProfileWithCyrillicWorkspaceRemovesBootstrapWithoutApplyingLaunchRestriction()
    {
        var workspace = Path.Combine(_paths.WorkspaceRoot, "Документы", "profile-test");
        var cache = Path.Combine(workspace, ".usvfs-bootstrap");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "runtime.tmp"), "cache");
        var userdata = Path.Combine(workspace, "userdata");
        Directory.CreateDirectory(userdata);
        File.WriteAllText(Path.Combine(userdata, "save"), "save");
        var profile = new ModProfile { WorkspacePath = workspace };
        var profiles = new List<ModProfile> { profile };
        try
        {
            Assert.Throws<InvalidOperationException>(() => UsvfsBootstrapPathResolver.Resolve(workspace));
            Assert.Null((await _manager.DeleteAsync(profiles, profile, () => Task.CompletedTask)).CleanupFailure);
            Assert.Empty(profiles);
            Assert.Contains(profile, _workspaceManager.DeletedProfiles);
            Assert.False(Directory.Exists(cache));
            Assert.Equal("save", File.ReadAllText(Path.Combine(userdata, "save")));
        }
        finally { Directory.Delete(Directory.GetParent(_paths.WorkspaceRoot)!.FullName, recursive: true); }
    }

    [Fact]
    public async Task DeleteProfileUnderCyrillicLauncherDirectoryRemovesWorkspaceAndKeepsSources()
    {
        var root = Path.Combine(_paths.ConfigDirectory, "Документы");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "source.txt"), "source");
        var paths = new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "Workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var manager = new ProfileManager(paths, builder);
        var profile = new ModProfile { GameInstallPath = game };
        var profiles = new List<ModProfile> { profile };
        try
        {
            var workspace = Path.Combine(paths.WorkspaceRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
            profile.WorkspacePath = workspace;
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, ".usvfs-bootstrap"));
            File.WriteAllText(Path.Combine(workspace, ".usvfs-bootstrap", "runtime.tmp"), "cache");
            // Keep drive-wide legacy cache cleanup outside this fixture.
            profile.GameInstallPath = string.Empty;
            Assert.Null((await manager.DeleteAsync(profiles, profile, () => Task.CompletedTask)).CleanupFailure);
            Assert.Empty(profiles);
            Assert.False(Directory.Exists(workspace));
            Assert.Equal("source", File.ReadAllText(Path.Combine(game, "source.txt")));
        }
        finally { Directory.Delete(_paths.ConfigDirectory, recursive: true); }
    }

    [Fact]
    public void CreateUsesUniqueNameAndStartsWithoutInheritedPaths()
    {
        var expectedBaseName = LocalizedText.Format(Strings.Profile_DefaultNameFormat, 2);
        var existing = new List<ModProfile> { new() { Name = expectedBaseName } };

        var created = _manager.Create(existing);

        Assert.Equal($"{expectedBaseName} (2)", created.Name);
        Assert.Empty(created.GameInstallPath);
        Assert.Empty(created.WorkspacePath);
        Assert.EndsWith("Mods", created.ModInstallPath);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, created.LaunchBackendKind);
    }

    [Fact]
    public async Task DeleteUnverifiedWorkspaceDoesNotRemoveItsBootstrapOrUserData()
    {
        var root = Path.Combine(_paths.ConfigDirectory, "Unrelated");
        var workspace = Path.Combine(root, "profile-test");
        Directory.CreateDirectory(Path.Combine(workspace, ".usvfs-bootstrap"));
        Directory.CreateDirectory(Path.Combine(workspace, "userdata"));
        File.WriteAllText(Path.Combine(workspace, ".usvfs-bootstrap", "valuable.dll"), "keep bootstrap");
        File.WriteAllText(Path.Combine(workspace, "userdata", "save"), "keep save");
        var profile = new ModProfile { Id = "test", WorkspacePath = workspace };
        var profiles = new List<ModProfile> { profile };
        var manager = new ProfileManager(_paths, new WorkspaceBuilder(_paths));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteAsync(profiles, profile, () => Task.CompletedTask));
            Assert.Single(profiles);
            Assert.Equal("keep bootstrap", File.ReadAllText(Path.Combine(workspace, ".usvfs-bootstrap", "valuable.dll")));
            Assert.Equal("keep save", File.ReadAllText(Path.Combine(workspace, "userdata", "save")));
        }
        finally { Directory.Delete(_paths.ConfigDirectory, recursive: true); }
    }

    [Fact]
    public void DuplicateCopiesConfigurationButUsesNewIdsAndWorkspace()
    {
        var source = new ModProfile
        {
            Name = "Zona",
            Description = "Description",
            IsDiscordStatusEnabled = false,
            GameInstallPath = @"D:\Games\STALKER",
            UsvfsExecutableOverrideRelativePath = @"bin\AnomalyDX10.exe",
            WorkspacePath = @"D:\OldWorkspace",
            TotalPlaytimeSeconds = 500,
            LastPlayedAt = DateTime.Now
        };
        source.Mods.Add(new ModEntry { Name = "Patch", SourcePath = @"D:\Mods\Patch", Order = 1 });

        var duplicate = _manager.Duplicate([source], source);

        Assert.Equal("Zona — копия", duplicate.Name);
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.WorkspacePath, duplicate.WorkspacePath);
        Assert.Empty(duplicate.WorkspacePath);
        Assert.EndsWith($"profile-{duplicate.Id}", _manager.GetProfileFolderPath(duplicate));
        Assert.EndsWith("Mods", duplicate.ModInstallPath);
        Assert.Equal(0, duplicate.TotalPlaytimeSeconds);
        Assert.Null(duplicate.LastPlayedAt);
        Assert.False(duplicate.IsDiscordStatusEnabled);
        Assert.Single(duplicate.Mods);
        Assert.NotSame(source.Mods[0], duplicate.Mods[0]);
        Assert.NotEqual(source.Mods[0].Id, duplicate.Mods[0].Id);
        Assert.Equal(source.Mods[0].SourcePath, duplicate.Mods[0].SourcePath);
        Assert.Equal(source.UsvfsExecutableOverrideRelativePath, duplicate.UsvfsExecutableOverrideRelativePath);
    }

    [Fact]
    public void DuplicateUsesStableAsciiWorkspaceName()
    {
        var source = new ModProfile { Name = new string('A', 100) + ": invalid." };

        var duplicate = _manager.Duplicate([source], source);
        Assert.Empty(duplicate.WorkspacePath);
        var directoryName = Path.GetFileName(_manager.GetProfileFolderPath(duplicate))!;

        Assert.Equal($"profile-{duplicate.Id}", directoryName);
        Assert.All(directoryName, character => Assert.True(character <= 0x7F));
    }

    [Fact]
    public async Task DeleteStandaloneProfileNeverDeletesModOrWorkspace()
    {
        var profile = new ModProfile { Name = "Standalone", IsStandalone = true, WorkspacePath = @"D:\Mods\Standalone" };
        var profiles = new List<ModProfile> { profile };

        var (selected, failure) = await _manager.DeleteAsync(profiles, profile, () => Task.CompletedTask);
        Assert.Null(failure);

        Assert.Null(selected);
        Assert.Empty(profiles);
        Assert.Empty(_workspaceManager.DeletedProfiles);
    }

    [Fact]
    public async Task DeleteUnavailableProfileDoesNotGuessOrDeleteWorkspace()
    {
        var profile = new ModProfile { SettingsLoadError = "missing settings", WorkspacePath = @"D:\unknown-workspace" };
        var profiles = new List<ModProfile> { profile };

        Assert.Null((await _manager.DeleteAsync(profiles, profile, () => Task.CompletedTask)).CleanupFailure);

        Assert.Empty(profiles);
        Assert.Empty(_workspaceManager.DeletedProfiles);
    }

    [Fact]
    public async Task DeleteOverlayProfileDeletesOnlyItsWorkspaceThenSelectsRemainder()
    {
        var removed = new ModProfile { Name = "Removed", GameInstallPath = @"D:\Game" };
        var remaining = new ModProfile { Name = "Remaining" };
        var profiles = new List<ModProfile> { removed, remaining };

        var (selected, failure) = await _manager.DeleteAsync(profiles, removed, () => Task.CompletedTask);
        Assert.Null(failure);

        Assert.Same(remaining, selected);
        Assert.Single(_workspaceManager.DeletedProfiles);
        Assert.Same(removed, _workspaceManager.DeletedProfiles[0]);
    }

    [Fact]
    public async Task DeleteOverlayProfileDeletesItsLocalAndLegacyUsvfsBootstrap()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "StalkerModLauncherProfileDeleteTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root, Path.Combine(root, "workspaces"), false);
            var manager = new ProfileManager(paths, _workspaceManager);
            var game = Path.Combine(root, "game");
            var removed = new ModProfile
            {
                Id = "removed-profile",
                Name = "Removed",
                GameInstallPath = game,
                WorkspacePath = Path.Combine(paths.WorkspaceRoot, "Removed")
            };
            var remaining = new ModProfile
            {
                Id = "remaining-profile",
                Name = "Remaining",
                GameInstallPath = game,
                WorkspacePath = Path.Combine(paths.WorkspaceRoot, "Remaining")
            };
            var removedBootstrap = Path.Combine(removed.WorkspacePath, ".usvfs-bootstrap");
            var remainingBootstrap = Path.Combine(remaining.WorkspacePath, ".usvfs-bootstrap");
            var removedLegacyBootstrap = GetSharedUsvfsBootstrapPath(removed.WorkspacePath, removed.Id);
            var remainingLegacyBootstrap = GetSharedUsvfsBootstrapPath(remaining.WorkspacePath, remaining.Id);
            Directory.CreateDirectory(removedBootstrap);
            Directory.CreateDirectory(remainingBootstrap);
            Directory.CreateDirectory(removedLegacyBootstrap);
            Directory.CreateDirectory(remainingLegacyBootstrap);
            File.WriteAllText(Path.Combine(removedBootstrap, "engine.dll"), "removed");
            File.WriteAllText(Path.Combine(remainingBootstrap, "engine.dll"), "remaining");
            File.WriteAllText(Path.Combine(removedLegacyBootstrap, "engine.dll"), "removed legacy");
            File.WriteAllText(Path.Combine(remainingLegacyBootstrap, "engine.dll"), "remaining legacy");
            var profiles = new List<ModProfile> { removed, remaining };

            Assert.Null((await manager.DeleteAsync(profiles, removed, () => Task.CompletedTask)).CleanupFailure);

            Assert.False(Directory.Exists(removedBootstrap));
            Assert.True(Directory.Exists(remainingBootstrap));
            Assert.False(Directory.Exists(removedLegacyBootstrap));
            Assert.True(Directory.Exists(remainingLegacyBootstrap));
            Assert.Single(profiles);
            Assert.Same(remaining, profiles[0]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteReportsCleanupFailureAfterPersistingRemoval()
    {
        var profile = new ModProfile { Name = "Keep me" };
        var profiles = new List<ModProfile> { profile };
        _workspaceManager.DeleteException = new IOException("Workspace is busy");

        var result = await _manager.DeleteAsync(profiles, profile, () =>
        {
            Assert.Empty(profiles);
            Assert.Empty(_workspaceManager.DeletedProfiles);
            return Task.CompletedTask;
        });

        Assert.IsType<IOException>(result.CleanupFailure);
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task FailedSettingsSaveKeepsProfileOrderAndWorkspaceSave()
    {
        var builder = new WorkspaceBuilder(_paths);
        var manager = new ProfileManager(_paths, builder);
        var kept = new ModProfile { Name = "Keep" };
        var profile = new ModProfile { Name = "Remove" };
        var profiles = new List<ModProfile> { kept, profile };
        using var store = new SettingsStore(_paths);
        try
        {
            profile.WorkspacePath = builder.EnsureProfileWorkspace(profile, string.Empty);
            var save = Path.Combine(profile.WorkspacePath, "userdata", "savedgames", "keep.sav");
            Directory.CreateDirectory(Path.GetDirectoryName(save)!);
            File.WriteAllText(save, "saved game");
            await store.SaveAsync(new AppSettings { Profiles = [.. profiles] });
            using (var locked = new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<SettingsPersistenceException>(() => manager.DeleteAsync(
                    profiles, profile, () => store.SaveAsync(new AppSettings { Profiles = [.. profiles] })));

            Assert.Equal(new[] { kept, profile }, profiles);
            Assert.Equal("saved game", File.ReadAllText(save));
            using var reopened = new SettingsStore(_paths);
            Assert.Contains((await reopened.LoadAsync()).Profiles, loaded => loaded.Id == profile.Id);

            var deletion = await manager.DeleteAsync(profiles, profile, async () =>
            {
                await reopened.SaveAsync(new AppSettings { Profiles = [.. profiles] });
                Assert.True(File.Exists(save));
            });
            Assert.Null(deletion.CleanupFailure);
            Assert.False(Directory.Exists(profile.WorkspacePath));
            Assert.Equal(kept.Id, Assert.Single((await reopened.LoadAsync()).Profiles).Id);
        }
        finally
        {
            if (Directory.Exists(_paths.ConfigDirectory))
                Directory.Delete(_paths.ConfigDirectory, recursive: true);
        }
    }

    [Fact]
    public void PrepareImportedAssignsDefaultsAndUniqueName()
    {
        var existing = new List<ModProfile> { new() { Name = "Imported" } };
        var imported = new ModProfile { Id = string.Empty, Name = "Imported", ExecutableRelativePath = string.Empty, IsRunning = true };

        _manager.PrepareImported(existing, imported);

        Assert.Equal("Imported (2)", imported.Name);
        Assert.False(string.IsNullOrWhiteSpace(imported.Id));
        Assert.Equal(@"bin\xr_3da.exe", imported.ExecutableRelativePath);
        Assert.False(imported.IsRunning);
    }

    [Fact]
    public void EnsureDefaultsMigratesLegacyProfileModFolderToSharedModsFolder()
    {
        var profile = new ModProfile { Id = "legacy-profile", GameInstallPath = @"D:\Game" };
        profile.ModInstallPath = _paths.GetLegacyProfileModInstallPath(profile.Id, profile.GameInstallPath);

        _manager.EnsureDefaults(profile);

        Assert.EndsWith("Mods", profile.ModInstallPath);
        Assert.DoesNotContain("profile-legacy-profile", profile.ModInstallPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetProfileFolderPathReturnsStandaloneModOrOverlayWorkspace()
    {
        var standaloneRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(standaloneRoot);
        try
        {
            var standalone = new ModProfile { IsStandalone = true };
            standalone.Mods.Add(new ModEntry { SourcePath = standaloneRoot, IsEnabled = true });
            var overlay = new ModProfile { WorkspacePath = @"D:\Workspace" };

            Assert.Equal(standaloneRoot, _manager.GetProfileFolderPath(standalone));
            Assert.Equal(@"D:\Workspace", _manager.GetProfileFolderPath(overlay));
        }
        finally
        {
            Directory.Delete(standaloneRoot);
        }
    }

    [Fact]
    public void EnsureProfileFolderPathPersistsFirstPathAcrossProfileRename()
    {
        var profile = new ModProfile { Name = "Профиль 1", GameInstallPath = @"D:\Game" };

        var first = _manager.EnsureProfileFolderPath(profile);
        profile.Name = "Профиль 2";
        var second = _manager.EnsureProfileFolderPath(profile);

        Assert.Equal(first, profile.WorkspacePath);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MoveToInsertionIndexReordersProfiles()
    {
        var first = new ModProfile { Name = "First" };
        var second = new ModProfile { Name = "Second" };
        var third = new ModProfile { Name = "Third" };
        var profiles = new System.Collections.ObjectModel.ObservableCollection<ModProfile>(
            [first, second, third]);

        var moved = ProfileManager.MoveToInsertionIndex(profiles, first, 3);

        Assert.True(moved);
        Assert.Equal(["Second", "Third", "First"], profiles.Select(profile => profile.Name));
    }

    private static string GetSharedUsvfsBootstrapPath(string workspacePath, string profileId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId)))
            [..16]
            .ToLowerInvariant();
        return Path.Combine(
            Path.GetDirectoryName(workspacePath)!,
            ".usvfs-bootstrap",
            key);
    }

    private sealed class FakeWorkspaceManager : IProfileWorkspaceManager
    {
        public List<ModProfile> DeletedProfiles { get; } = [];
        public Exception? DeleteException { get; set; }

        public string EnsureProfileWorkspace(ModProfile profile, string gamePath, IProgress<string>? progress = null)
        {
            return string.IsNullOrWhiteSpace(profile.WorkspacePath)
                ? Path.Combine(Path.GetTempPath(), $"profile-{profile.Id}")
                : profile.WorkspacePath;
        }

        public void ValidateProfileWorkspaceDeletion(ModProfile profile, string gamePath) { }

        public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            DeletedProfiles.Add(profile);
        }

    }
}
