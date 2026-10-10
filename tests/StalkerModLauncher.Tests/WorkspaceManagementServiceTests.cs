using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class WorkspaceManagementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InspectAsyncReadsStatisticsFromManifestWithoutScanningWorkspace()
    {
        var workspace = Path.Combine(_root, "workspaces", "profile");
        Directory.CreateDirectory(Path.Combine(workspace, "current"));
        File.WriteAllText(Path.Combine(workspace, "current", "fsgame.ltx"), "12345");
        File.WriteAllText(
            Path.Combine(workspace, "build-manifest.json"),
            """
            {
              "BuiltAtUtc": "2026-06-15T00:00:00Z",
              "HasStatistics": true,
              "FileCount": 12,
              "HardLinkCount": 7,
              "SymbolicLinkCount": 4,
              "LocalFileCount": 1,
              "LogicalSizeBytes": 12345,
              "PhysicalSizeBytes": 5
            }
            """);
        var profile = new ModProfile { WorkspacePath = workspace };

        var status = await WorkspaceManagementService.InspectAsync(profile);

        Assert.True(status.Exists);
        Assert.Equal(12, status.FileCount);
        Assert.Equal(12345, status.LogicalSizeBytes);
        Assert.Equal(1, status.LocalFileCount);
        Assert.Equal(7, status.HardLinkCount);
        Assert.Equal(4, status.SymbolicLinkCount);
    }

    [Fact]
    public async Task InspectAsyncReturnsImmediatelyWithoutDetailedStatisticsForOldManifest()
    {
        var workspace = Path.Combine(_root, "workspaces", "old-profile");
        Directory.CreateDirectory(Path.Combine(workspace, "current"));
        for (var index = 0; index < 5000; index++)
        {
            File.WriteAllText(Path.Combine(workspace, "current", $"{index}.bin"), "test");
        }
        File.WriteAllText(Path.Combine(workspace, "build-manifest.json"), """{"BuiltAtUtc":"2026-06-15T00:00:00Z"}""");
        var profile = new ModProfile { WorkspacePath = workspace };
        var started = DateTime.UtcNow;

        var status = await WorkspaceManagementService.InspectAsync(profile);

        Assert.False(status.StatisticsAvailable);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MoveAsyncMovesUserDataButNotCurrentCache()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(_root, "workspaces", "old-profile");
        Directory.CreateDirectory(Path.Combine(oldWorkspace, "current"));
        File.WriteAllText(Path.Combine(oldWorkspace, ".stalker-launcher-workspace"), "marker");
        File.WriteAllText(Path.Combine(oldWorkspace, "current", "cache.bin"), "cache");
        Directory.CreateDirectory(Path.Combine(oldWorkspace, "userdata", "savedgames"));
        File.WriteAllText(Path.Combine(oldWorkspace, "userdata", "savedgames", "save.sav"), "save");
        var profile = new ModProfile
        {
            Id = "12345678",
            Name = "Moved profile",
            WorkspacePath = oldWorkspace
        };
        var destinationRoot = Path.Combine(_root, "destination");

        var result = await service.MoveAsync(profile, destinationRoot, new Progress<string>());

        Assert.True(result.WasMoved);
        Assert.Null(result.CleanupFailure);
        Assert.True(File.Exists(Path.Combine(profile.WorkspacePath, "userdata", "savedgames", "save.sav")));
        Assert.False(Directory.Exists(Path.Combine(profile.WorkspacePath, "current")));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Theory]
    [InlineData(LaunchBackendKind.LinkedWorkspace, true)]
    [InlineData(LaunchBackendKind.LinkedWorkspace, false)]
    [InlineData(LaunchBackendKind.VirtualFileSystem, true)]
    [InlineData(LaunchBackendKind.VirtualFileSystem, false)]
    public async Task MoveAsyncPreservesNewestWritableSettings(LaunchBackendKind backend, bool workspaceIsNewer)
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(paths.WorkspaceRoot, "profile-writable-settings");
        File.WriteAllText(CreateDirectoryAndReturnFile(oldWorkspace, ".stalker-launcher-workspace"), "marker");
        var storedWriteTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            var workspaceFile = Path.Combine(oldWorkspace, "current", rule.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
            File.WriteAllText(workspaceFile, "workspace settings");
            File.SetLastWriteTimeUtc(workspaceFile, storedWriteTime.AddMinutes(workspaceIsNewer ? 1 : -1));
            var storedFile = Path.Combine(oldWorkspace, rule.StorageRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(storedFile)!);
            File.WriteAllText(storedFile, "stored settings");
            File.SetLastWriteTimeUtc(storedFile, storedWriteTime);
        }
        var profile = new ModProfile
        {
            Id = "writable-settings",
            WorkspacePath = oldWorkspace,
            LaunchBackendKind = backend
        };

        var result = await service.MoveAsync(profile, Path.Combine(_root, "destination-writable"), new Progress<string>());

        Assert.True(result.WasMoved);
        Assert.Null(result.CleanupFailure);
        Assert.Equal(result.DestinationPath, profile.WorkspacePath);
        foreach (var rule in ProfileWritableGameFiles.Rules)
        {
            Assert.Equal(
                workspaceIsNewer ? "workspace settings" : "stored settings",
                File.ReadAllText(Path.Combine(result.DestinationPath, rule.StorageRelativePath)));
        }
        Assert.False(Directory.Exists(Path.Combine(result.DestinationPath, "current")));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Fact]
    public async Task MoveAsyncCapturesWritableSettingsWithoutExistingUserData()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(paths.WorkspaceRoot, "profile-first-move");
        File.WriteAllText(CreateDirectoryAndReturnFile(oldWorkspace, ".stalker-launcher-workspace"), "marker");
        var rule = ProfileWritableGameFiles.Rules[0];
        var workspaceFile = Path.Combine(oldWorkspace, "current", rule.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, "latest settings");
        var profile = new ModProfile { Id = "first-move", WorkspacePath = oldWorkspace };

        var result = await service.MoveAsync(profile, Path.Combine(_root, "destination-first-move"), new Progress<string>());

        Assert.Null(result.CleanupFailure);
        Assert.Equal("latest settings", File.ReadAllText(Path.Combine(result.DestinationPath, rule.StorageRelativePath)));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Fact]
    public async Task MoveAsyncKeepsOldWorkspaceWhenWritableSettingsCannotBeCaptured()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(paths.WorkspaceRoot, "profile-locked-settings");
        File.WriteAllText(CreateDirectoryAndReturnFile(oldWorkspace, ".stalker-launcher-workspace"), "marker");
        var rule = ProfileWritableGameFiles.Rules[0];
        var workspaceFile = Path.Combine(oldWorkspace, "current", rule.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, "latest settings");
        var profile = new ModProfile { Id = "locked-settings", WorkspacePath = oldWorkspace };
        var destinationRoot = Path.Combine(_root, "destination-locked-settings");

        using (File.Open(workspaceFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.MoveAsync(profile, destinationRoot, new Progress<string>()));
        }

        Assert.Equal(oldWorkspace, profile.WorkspacePath);
        Assert.Equal("latest settings", File.ReadAllText(workspaceFile));
        Assert.Empty(Directory.EnumerateDirectories(destinationRoot));
    }

    [Fact]
    public async Task MoveAsyncDoesNotCopyTemporaryUsvfsBootstrap()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(_root, "workspaces", "usvfs-profile");
        File.WriteAllText(
            CreateDirectoryAndReturnFile(oldWorkspace, ".stalker-launcher-workspace"),
            "marker");
        File.WriteAllText(
            CreateDirectoryAndReturnFile(Path.Combine(oldWorkspace, "userdata", "savedgames"), "save.sav"),
            "save");
        File.WriteAllText(
            CreateDirectoryAndReturnFile(Path.Combine(oldWorkspace, "userdata", "usvfs-bootstrap", "bin"), "xrEngine.exe"),
            "legacy cache");
        File.WriteAllText(
            CreateDirectoryAndReturnFile(Path.Combine(oldWorkspace, ".usvfs-bootstrap", "bin"), "xrEngine.exe"),
            "cache");
        var profile = new ModProfile
        {
            Id = "87654321",
            Name = "USVFS profile",
            WorkspacePath = oldWorkspace,
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };

        var result = await service.MoveAsync(
            profile,
            Path.Combine(_root, "destination-usvfs"),
            new Progress<string>());

        Assert.True(result.WasMoved);
        Assert.Null(result.CleanupFailure);
        Assert.True(File.Exists(Path.Combine(profile.WorkspacePath, "userdata", "savedgames", "save.sav")));
        Assert.False(Directory.Exists(Path.Combine(profile.WorkspacePath, "userdata", "usvfs-bootstrap")));
        Assert.False(Directory.Exists(Path.Combine(profile.WorkspacePath, ".usvfs-bootstrap")));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Fact]
    public async Task MoveAsyncDoesNotDeleteDestinationWhenItIsInManagedWorkspaceRoot()
    {
        var destinationRoot = Path.Combine(_root, "managed-destination");
        var paths = new AppPaths(_root, destinationRoot, false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldRoot = Path.Combine(_root, "custom-old-root");
        Directory.CreateDirectory(oldRoot);
        File.WriteAllText(
            Path.Combine(oldRoot, ".stalker-launcher-workspace-root"),
            "Managed workspace root created by CORDON.");
        var profile = new ModProfile
        {
            Id = "managed-destination-profile",
            Name = "Managed destination profile"
        };
        var oldWorkspace = Path.Combine(
            oldRoot,
            $"profile-{profile.Id}");
        Directory.CreateDirectory(Path.Combine(oldWorkspace, "userdata", "savedgames"));
        File.WriteAllText(
            Path.Combine(oldWorkspace, ".stalker-launcher-workspace"),
            "marker");
        File.WriteAllText(
            Path.Combine(oldWorkspace, "userdata", "savedgames", "save.sav"),
            "save");
        profile.WorkspacePath = oldWorkspace;

        var result = await service.MoveAsync(profile, destinationRoot, new Progress<string>());

        Assert.True(result.WasMoved);
        Assert.Null(result.CleanupFailure);
        Assert.Equal(result.DestinationPath, profile.WorkspacePath);
        Assert.True(File.Exists(Path.Combine(
            result.DestinationPath,
            "userdata",
            "savedgames",
            "save.sav")));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Fact]
    public async Task MoveAsyncKeepsSuccessfulMoveWhenOldWorkspaceCleanupFailsAndCanRetryCleanup()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "base.bin"), "base");
        var paths = new AppPaths(_root, Path.Combine(_root, "workspaces"), false);
        var service = new WorkspaceManagementService(new WorkspaceBuilder(paths));
        var oldWorkspace = Path.Combine(_root, "workspaces", "profile-locked-profile");
        Directory.CreateDirectory(Path.Combine(oldWorkspace, "current"));
        File.WriteAllText(Path.Combine(oldWorkspace, ".stalker-launcher-workspace"), "marker");
        var lockedFile = Path.Combine(oldWorkspace, "current", "locked.bin");
        File.WriteAllText(lockedFile, "locked");
        Directory.CreateDirectory(Path.Combine(oldWorkspace, "userdata"));
        File.WriteAllText(Path.Combine(oldWorkspace, "userdata", "user.ltx"), "settings");
        var profile = new ModProfile
        {
            Id = "locked-profile",
            Name = "Locked profile",
            GameInstallPath = game,
            WorkspacePath = oldWorkspace
        };
        var destinationRoot = Path.Combine(_root, "destination-locked");

        WorkspaceMoveResult result;
        using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await service.MoveAsync(profile, destinationRoot, new Progress<string>());
        }

        Assert.True(result.WasMoved);
        Assert.NotNull(result.CleanupFailure);
        Assert.Equal(result.DestinationPath, profile.WorkspacePath);
        Assert.True(File.Exists(Path.Combine(result.DestinationPath, "userdata", "user.ltx")));
        Assert.True(Directory.Exists(oldWorkspace));

        var retryFailure = await service.RetryOldWorkspaceCleanupAsync(profile, oldWorkspace);

        Assert.Null(retryFailure);
        Assert.False(Directory.Exists(oldWorkspace));
    }

    private static string CreateDirectoryAndReturnFile(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
