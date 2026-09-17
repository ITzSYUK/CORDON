using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class WorkspaceManagementService
{
    private readonly WorkspaceBuilder _workspaceBuilder;

    public WorkspaceManagementService(WorkspaceBuilder workspaceBuilder)
    {
        _workspaceBuilder = workspaceBuilder;
    }

    public static Task<WorkspaceStatus> InspectAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Inspect(profile, cancellationToken), cancellationToken);
    }

    public void ClearCache(ModProfile profile, IProgress<string>? progress = null)
    {
        _workspaceBuilder.ClearProfileWorkspaceCache(profile, profile.GameInstallPath, progress);
    }

    public async Task RebuildAsync(ModProfile profile, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        ClearCache(profile, progress);
        var result = await _workspaceBuilder.BuildAsync(
            profile.GameInstallPath,
            profile,
            progress,
            cancellationToken: cancellationToken);
        profile.WorkspacePath = result.ProfileWorkspacePath;
        profile.ExecutableRelativePath = result.ExecutableRelativePath;
        profile.WorkingDirectoryRelative = result.WorkingDirectoryRelative;
    }

    public async Task<WorkspaceMoveResult> MoveAsync(
        ModProfile profile,
        string destinationRoot,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(
            () => PrepareMove(profile, destinationRoot, progress, cancellationToken),
            cancellationToken);
        if (!result.WasMoved)
        {
            return result;
        }

        // ModProfile is observed by WPF. Change it only after returning to the
        // caller's synchronization context, never from the Task.Run worker.
        profile.WorkspacePath = result.DestinationPath;

        var cleanupFailure = await Task.Run(
            () => TryCleanupOldWorkspace(profile, result.PreviousWorkspacePath),
            CancellationToken.None);
        return result with { CleanupFailure = cleanupFailure };
    }

    public Task<Exception?> RetryOldWorkspaceCleanupAsync(
        ModProfile profile,
        string oldWorkspace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldWorkspace) || !Directory.Exists(oldWorkspace))
        {
            return Task.FromResult<Exception?>(null);
        }

        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
            FileSystemSafety.IsSameDirectory(profile.WorkspacePath, oldWorkspace))
        {
            return Task.FromResult<Exception?>(new InvalidOperationException(
                LocalizedText.Format(Strings.Workspace_ActiveCannotDeleteFormat, oldWorkspace)));
        }

        return Task.Run(
            () => TryCleanupOldWorkspace(profile, oldWorkspace),
            cancellationToken);
    }

    private static WorkspaceMoveResult PrepareMove(
        ModProfile profile,
        string destinationRoot,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (profile.IsStandalone)
        {
            throw new InvalidOperationException(Strings.Profile_StandaloneNoWorkspace);
        }

        if (FileSystemSafety.IsFileSystemRoot(destinationRoot))
        {
            throw new InvalidOperationException(Strings.Workspace_DriveRootForbidden);
        }

        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(
            Path.Combine(destinationRoot, WorkspaceBuilder.RootMarkerFileName),
            "Managed workspace root created by CORDON.");
        var destination = Path.Combine(destinationRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        var oldWorkspace = profile.WorkspacePath;
        if (!string.IsNullOrWhiteSpace(oldWorkspace) && FileSystemSafety.IsSameDirectory(oldWorkspace, destination))
        {
            return new WorkspaceMoveResult(destination, oldWorkspace, WasMoved: false);
        }

        if (!string.IsNullOrWhiteSpace(oldWorkspace) &&
            (FileSystemSafety.IsDirectoryInside(destination, oldWorkspace) ||
             FileSystemSafety.IsDirectoryInside(oldWorkspace, destination)))
        {
            throw new InvalidOperationException(Strings.Workspace_NestedDestination);
        }

        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Workspace_DestinationExistsFormat, destination));
        }

        var temporary = destination + $".moving-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporary);
        try
        {
            File.WriteAllText(
                Path.Combine(temporary, ".stalker-launcher-workspace"),
                "Managed by CORDON. It is safe for the launcher to recreate the 'current' subfolder.");

            var sourceUserData = string.IsNullOrWhiteSpace(oldWorkspace) ? string.Empty : Path.Combine(oldWorkspace, "userdata");
            var targetUserData = Path.Combine(temporary, "userdata");
            if (Directory.Exists(sourceUserData))
            {
                progress.Report(Strings.Workspace_CopyingUserData);
                CopyDirectory(
                    sourceUserData,
                    targetUserData,
                    profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
                        ? "usvfs-bootstrap"
                        : null,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
            progress.Report(LocalizedText.Format(Strings.Workspace_MovedFormat, destination));
            return new WorkspaceMoveResult(destination, oldWorkspace, WasMoved: true);
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            throw;
        }
    }

    private Exception? TryCleanupOldWorkspace(ModProfile profile, string? oldWorkspace)
    {
        if (string.IsNullOrWhiteSpace(oldWorkspace) || !Directory.Exists(oldWorkspace))
        {
            return null;
        }

        try
        {
            LegacyProfileDataAlias.Delete(oldWorkspace, profile.Id);
            _workspaceBuilder.DeleteProfileWorkspaceAtPath(
                CreateCleanupProfile(profile, oldWorkspace),
                profile.GameInstallPath,
                oldWorkspace);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static ModProfile CreateCleanupProfile(ModProfile source, string workspacePath)
    {
        var cleanupProfile = new ModProfile
        {
            Id = source.Id,
            Name = source.Name,
            WorkspacePath = workspacePath,
            GameInstallPath = source.GameInstallPath
        };

        foreach (var mod in source.Mods)
        {
            cleanupProfile.Mods.Add(new ModEntry
            {
                Name = mod.Name,
                SourcePath = mod.SourcePath,
                IsEnabled = mod.IsEnabled,
                Order = mod.Order
            });
        }

        return cleanupProfile;
    }

    private static void CopyDirectory(
        string source,
        string destination,
        string? excludedTopLevelDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (IsExcluded(relative, excludedTopLevelDirectory))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (IsExcluded(relative, excludedTopLevelDirectory))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static bool IsExcluded(string relativePath, string? excludedTopLevelDirectory)
    {
        if (string.IsNullOrWhiteSpace(excludedTopLevelDirectory))
        {
            return false;
        }

        var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var topLevel = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return string.Equals(topLevel, excludedTopLevelDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceStatus Inspect(ModProfile profile, CancellationToken cancellationToken)
    {
        var workspace = profile.WorkspacePath;
        var current = string.IsNullOrWhiteSpace(workspace) ? string.Empty : Path.Combine(workspace, "current");
        var rootExists = !string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace);
        var currentExists = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current);
        var manifestExists = rootExists && File.Exists(Path.Combine(workspace, "build-manifest.json"));
        if (string.IsNullOrWhiteSpace(current) || !currentExists)
        {
            return WorkspaceStatus.Missing(workspace);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manifest = ReadManifest(workspace);
        return manifest is null
            ? new WorkspaceStatus(workspace, true, 0, 0, 0, 0, 0, 0, null, false, rootExists, currentExists, manifestExists)
            : new WorkspaceStatus(
                workspace,
                true,
                manifest.LogicalSizeBytes,
                manifest.PhysicalSizeBytes,
                manifest.FileCount,
                manifest.SymbolicLinkCount,
                manifest.HardLinkCount,
                manifest.LocalFileCount,
                manifest.BuiltAtUtc,
                manifest.HasStatistics,
                rootExists,
                currentExists,
                manifestExists);
    }

    private static WorkspaceBuildManifest? ReadManifest(string workspace)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceBuildManifest>(
                File.ReadAllText(Path.Combine(workspace, "build-manifest.json")));
        }
        catch
        {
            return null;
        }
    }
}

public sealed record WorkspaceMoveResult(
    string DestinationPath,
    string? PreviousWorkspacePath,
    bool WasMoved,
    Exception? CleanupFailure = null);
