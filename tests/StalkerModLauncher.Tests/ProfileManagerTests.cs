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
        Assert.EndsWith($"profile-{duplicate.Id}", duplicate.WorkspacePath);
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
        var directoryName = Path.GetFileName(duplicate.WorkspacePath);

        Assert.Equal($"profile-{duplicate.Id}", directoryName);
        Assert.All(directoryName, character => Assert.True(character <= 0x7F));
    }

    [Fact]
    public void DeleteStandaloneProfileNeverDeletesModOrWorkspace()
    {
        var profile = new ModProfile { Name = "Standalone", IsStandalone = true, WorkspacePath = @"D:\Mods\Standalone" };
        var profiles = new List<ModProfile> { profile };

        var selected = _manager.Delete(profiles, profile);

        Assert.Null(selected);
        Assert.Empty(profiles);
        Assert.Empty(_workspaceManager.DeletedProfiles);
    }

    [Fact]
    public void DeleteOverlayProfileDeletesOnlyItsWorkspaceThenSelectsRemainder()
    {
        var removed = new ModProfile { Name = "Removed", GameInstallPath = @"D:\Game" };
        var remaining = new ModProfile { Name = "Remaining" };
        var profiles = new List<ModProfile> { removed, remaining };

        var selected = _manager.Delete(profiles, removed);

        Assert.Same(remaining, selected);
        Assert.Single(_workspaceManager.DeletedProfiles);
        Assert.Same(removed, _workspaceManager.DeletedProfiles[0]);
    }

    [Fact]
    public void DeleteOverlayProfileDeletesItsLocalAndLegacyUsvfsBootstrap()
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

            manager.Delete(profiles, removed);

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
    public void DeleteKeepsProfileWhenWorkspaceDeletionFails()
    {
        var profile = new ModProfile { Name = "Keep me" };
        var profiles = new List<ModProfile> { profile };
        _workspaceManager.DeleteException = new IOException("Workspace is busy");

        Assert.Throws<IOException>(() => _manager.Delete(profiles, profile));

        Assert.Single(profiles);
        Assert.Same(profile, profiles[0]);
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

        public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            DeletedProfiles.Add(profile);
        }

        public void ClearProfileWorkspaceCache(ModProfile profile, string gamePath)
        {
        }
    }
}
