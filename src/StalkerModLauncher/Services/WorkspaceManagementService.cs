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

    public async Task<WorkspaceMoveResult> MoveAsync(
        ModProfile profile,
        string destinationRoot,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        EnsureIdle(profile);
        profile.IsPreparing = true;
        try
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
        finally { profile.IsPreparing = false; }
    }

    public Task<Exception?> RetryOldWorkspaceCleanupAsync(
        ModProfile profile,
        string oldWorkspace,
        CancellationToken cancellationToken = default)
    {
        EnsureIdle(profile);
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

        var finalDestination = Path.Combine(destinationRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        WorkspaceBuilder.ValidateProfileLocation(profile, finalDestination, profile.GameInstallPath);
        var sourceRoots = new[] { profile.GameInstallPath, profile.Mo2OverwritePath }
            .Concat(profile.Mods.Select(mod => mod.SourcePath));
        if (sourceRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Any(source =>
                FileSystemSafety.IsDirectoryInside(destinationRoot, source) ||
                FileSystemSafety.IsDirectoryInside(source, destinationRoot)))
            throw new InvalidOperationException(Strings.ProfileStorage_SourceOverlap);

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

            if (!string.IsNullOrWhiteSpace(oldWorkspace))
            {
                ProfileWritableGameFileStore.CaptureFromWorkspace(
                    Path.Combine(oldWorkspace, ".usvfs-bootstrap"),
                    oldWorkspace,
                    progress);
            }

            var sourceUserData = string.IsNullOrWhiteSpace(oldWorkspace) ? string.Empty : Path.Combine(oldWorkspace, "userdata");
            var targetUserData = Path.Combine(temporary, "userdata");
            if (Directory.Exists(sourceUserData))
            {
                progress.Report(Strings.Workspace_CopyingUserData);
                CopyDirectory(
                    sourceUserData,
                    targetUserData,
                    "usvfs-bootstrap",
                    cancellationToken);
            }

            var diagnostics = string.IsNullOrWhiteSpace(oldWorkspace) ? string.Empty : Path.Combine(oldWorkspace, "diagnostics");
            if (Directory.Exists(diagnostics))
                CopyDirectory(diagnostics, Path.Combine(temporary, "diagnostics"), null, cancellationToken);

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
            GameInstallPath = source.GameInstallPath,
            Mo2OverwritePath = source.Mo2OverwritePath
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
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, source));
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(entry.Name, excludedTopLevelDirectory)) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, entry.FullName));
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
                CopyDirectory(entry.FullName, target, null, cancellationToken);
            else
                File.Copy(entry.FullName, target, overwrite: false);
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

    private static void EnsureIdle(ModProfile profile)
    {
        if (profile.IsRunning || profile.IsPreparing)
            throw new InvalidOperationException(Strings.ProfileStorage_Busy);
    }

    private static WorkspaceStatus Inspect(ModProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WorkspaceStatus.Missing(profile.WorkspacePath);
    }
}

public sealed record WorkspaceMoveResult(
    string DestinationPath,
    string? PreviousWorkspacePath,
    bool WasMoved,
    Exception? CleanupFailure = null);
