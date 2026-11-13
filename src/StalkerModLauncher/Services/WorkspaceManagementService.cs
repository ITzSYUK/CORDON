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
        EnsureIdle(profile);
        _workspaceBuilder.ClearProfileWorkspaceCache(profile, profile.GameInstallPath, progress);
    }

    public async Task RebuildAsync(ModProfile profile, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        EnsureIdle(profile);
        profile.IsPreparing = true;
        try
        {
            _workspaceBuilder.ClearProfileWorkspaceCache(profile, profile.GameInstallPath, progress);
            var result = await _workspaceBuilder.BuildAsync(
                profile.GameInstallPath, profile, progress, cancellationToken: cancellationToken);
            profile.WorkspacePath = result.ProfileWorkspacePath;
            profile.ExecutableRelativePath = result.ExecutableRelativePath;
            profile.WorkingDirectoryRelative = result.WorkingDirectoryRelative;
        }
        finally
        {
            profile.IsPreparing = false;
        }
    }

    public Task<WorkspaceMoveResult> MoveAsync(
        ModProfile profile, string destinationRoot, IProgress<string> progress,
        Func<Task> persistSettings, CancellationToken cancellationToken = default) =>
        TransferAsync(profile, destinationRoot, progress, persistSettings, false, cancellationToken);

    internal Task<WorkspaceMoveResult> CopyToLocalAsync(
        ModProfile profile, string destinationRoot, IProgress<string> progress, Func<Task> persistSettings) =>
        TransferAsync(profile, destinationRoot, progress, persistSettings, true, CancellationToken.None);

    private async Task<WorkspaceMoveResult> TransferAsync(
        ModProfile profile,
        string destinationRoot,
        IProgress<string> progress,
        Func<Task> persistSettings,
        bool copyOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistSettings);
        EnsureIdle(profile);
        profile.IsPreparing = true;
        try
        {
            var result = await Task.Run(
                () => PrepareMove(profile, destinationRoot, progress, copyOnly, cancellationToken),
                cancellationToken);
            if (!result.WasMoved)
            {
                return result;
            }

            // ModProfile is observed by WPF. Change it only after returning to the
            // caller's synchronization context, never from the Task.Run worker.
            profile.WorkspacePath = result.DestinationPath;
            try { await persistSettings(); }
            catch
            {
                // A partial save journal may already reference the copy; keep both directories.
                profile.WorkspacePath = result.PreviousWorkspacePath ?? string.Empty;
                throw;
            }

            if (copyOnly) return result;

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
        bool copyOnly,
        CancellationToken cancellationToken)
    {
        if (profile.IsStandalone)
        {
            throw new InvalidOperationException(Strings.Profile_StandaloneNoWorkspace);
        }

        if (!copyOnly && !string.IsNullOrWhiteSpace(profile.WorkspacePath) && !Directory.Exists(profile.WorkspacePath))
            throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_WorkspaceUnavailableFormat, profile.WorkspacePath));

        if (FileSystemSafety.IsFileSystemRoot(destinationRoot))
        {
            throw new InvalidOperationException(Strings.Workspace_DriveRootForbidden);
        }

        var finalDestination = Path.Combine(destinationRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        WorkspaceBuilder.ValidateProfileLocation(profile, finalDestination, profile.GameInstallPath);
        if (profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem)
            _ = UsvfsBootstrapPathResolver.Resolve(finalDestination);
        var sourceRoots = new[] { profile.GameInstallPath, profile.Mo2OverwritePath }
            .Concat(profile.Mods.Select(mod => mod.SourcePath));
        if (sourceRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Any(source =>
                FileSystemSafety.IsDirectoryInside(destinationRoot, source) ||
                FileSystemSafety.IsDirectoryInside(source, destinationRoot)))
            throw new InvalidOperationException(Strings.ProfileStorage_SourceOverlap);

        var destination = finalDestination;
        var oldWorkspace = profile.WorkspacePath;
        var sameWorkspace = !string.IsNullOrWhiteSpace(oldWorkspace) &&
                            FileSystemSafety.IsSameDirectory(oldWorkspace, destination);
        if (!sameWorkspace && !string.IsNullOrWhiteSpace(oldWorkspace) &&
            (FileSystemSafety.IsDirectoryInside(destination, oldWorkspace) ||
             FileSystemSafety.IsDirectoryInside(oldWorkspace, destination)))
        {
            throw new InvalidOperationException(Strings.Workspace_NestedDestination);
        }

        if (copyOnly)
        {
            if (WorkspaceBuilder.HasDirectoryLinks(destinationRoot))
                throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, destinationRoot));
            var conflict = Directory.Exists(destinationRoot)
                ? Directory.EnumerateFileSystemEntries(destinationRoot)
                    .FirstOrDefault(path => WorkspaceBuilder.IsGeneratedWorkspaceOwnedByProfile(profile, path))
                : null;
            if (conflict is not null)
                throw new IOException(LocalizedText.Format(Strings.Workspace_DestinationExistsFormat, conflict));
            WorkspaceBuilder.EnsureWorkspaceRootMarker(destinationRoot);
        }
        else
        {
            Directory.CreateDirectory(destinationRoot);
            File.WriteAllText(
                Path.Combine(destinationRoot, WorkspaceBuilder.RootMarkerFileName),
                "Managed workspace root created by CORDON.");
        }
        if (!copyOnly && sameWorkspace)
        {
            return new WorkspaceMoveResult(destination, oldWorkspace, WasMoved: false);
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

            if (!copyOnly && !string.IsNullOrWhiteSpace(oldWorkspace))
            {
                ProfileWritableGameFileStore.CaptureFromWorkspace(
                    Path.Combine(oldWorkspace, ".usvfs-bootstrap"),
                    oldWorkspace,
                    progress);
                ProfileWritableGameFileStore.CaptureLinkedWorkspace(
                    oldWorkspace, oldWorkspace, profile.LaunchBackendKind == LaunchBackendKind.LinkedWorkspace, progress);
            }

            var sourceUserData = string.IsNullOrWhiteSpace(oldWorkspace) ? string.Empty : Path.Combine(oldWorkspace, "userdata");
            var targetUserData = Path.Combine(temporary, "userdata");
            if (copyOnly ? HasUserData(sourceUserData) : Directory.Exists(sourceUserData))
            {
                progress.Report(Strings.Workspace_CopyingUserData);
                if (copyOnly && WorkspaceBuilder.HasDirectoryLinks(sourceUserData))
                    throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, sourceUserData));
                CopyDirectory(
                    sourceUserData,
                    targetUserData,
                    copyOnly ? null : "usvfs-bootstrap",
                    cancellationToken);
            }

            if (copyOnly && !string.IsNullOrWhiteSpace(oldWorkspace))
            {
                FileSystemSafety.EnsureNoDirectoryLinks(oldWorkspace);
                ProfileWritableGameFileStore.CaptureFromWorkspace(
                    Path.Combine(oldWorkspace, ".usvfs-bootstrap"), temporary, progress);
                ProfileWritableGameFileStore.CaptureLinkedWorkspace(
                    oldWorkspace, temporary, profile.LaunchBackendKind == LaunchBackendKind.LinkedWorkspace, progress);
            }

            var diagnostics = string.IsNullOrWhiteSpace(oldWorkspace) ? string.Empty : Path.Combine(oldWorkspace, "diagnostics");
            if (!copyOnly && Directory.Exists(diagnostics))
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

    private static bool HasUserData(string path)
    {
        if (path.Length == 0) return false;
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
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
