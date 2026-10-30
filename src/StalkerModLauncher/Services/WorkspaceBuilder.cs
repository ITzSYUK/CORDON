using System.IO;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public interface IProfileWorkspaceManager
{
    string EnsureProfileWorkspace(ModProfile profile, string gamePath, IProgress<string>? progress = null);
    void DeleteProfileWorkspace(ModProfile profile, string gamePath);
}

public sealed class WorkspaceBuilder : IProfileWorkspaceManager
{
    private const string MarkerFileName = ".stalker-launcher-workspace";
    internal const string RootMarkerFileName = ".stalker-launcher-workspace-root";
    private readonly AppPaths _paths;
    public WorkspaceBuilder(AppPaths paths)
    {
        _paths = paths;
    }

    public static string GetSavedGamesPath(ModProfile profile)
    {
        var directories = ProfileDataPathResolver.GetSavedGameDirectories(profile);
        return directories.Count == 0 ? string.Empty : directories[0];
    }

    public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
    {
        var workspacePaths = FindGeneratedWorkspacePaths(profile, gamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath) && Directory.Exists(profile.WorkspacePath))
        {
            workspacePaths.Add(Path.GetFullPath(profile.WorkspacePath));
        }

        foreach (var workspacePath in workspacePaths)
        {
            DeleteProfileWorkspaceAtPath(profile, gamePath, workspacePath);
        }
    }

    internal void DeleteProfileWorkspaceAtPath(
        ModProfile profile,
        string gamePath,
        string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
        {
            return;
        }

        if (profile.IsRunning) throw new InvalidOperationException(Strings.ProfileStorage_Busy);
        ValidateProfileLocation(profile, workspacePath, gamePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var allowedRoot = FindAllowedWorkspaceParent(fullWorkspacePath, gamePath);
        if (allowedRoot is null)
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_WorkspaceDeleteOutsideRootFormat,
                fullWorkspacePath));
        }

        var markerPath = Path.Combine(fullWorkspacePath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            RestoreGeneratedWorkspaceMarker(profile, fullWorkspacePath, allowedRoot);
        }

        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_WorkspaceMarkerMissingFormat,
                fullWorkspacePath));
        }

        WorkspaceMaterializer.DeleteWorkspaceContents(
            fullWorkspacePath,
            allowedRoot,
            () => WorkspaceSourceScanner.Capture(gamePath, profile, CancellationToken.None));
    }

    public string EnsureProfileWorkspace(
        ModProfile profile,
        string gamePath,
        IProgress<string>? progress = null)
    {
        var preferredRoot = _paths.GetPreferredWorkspaceRoot(gamePath);
        var managedRoots = _paths.GetManagedWorkspaceRoots(gamePath);

        var existingGeneratedWorkspace = FindGeneratedWorkspacePaths(profile, gamePath)
            .OrderByDescending(path =>
                !string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
                FileSystemSafety.IsSameDirectory(path, profile.WorkspacePath))
            .ThenByDescending(path => File.Exists(Path.Combine(path, MarkerFileName)))
            .ThenByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (existingGeneratedWorkspace is not null)
        {
            existingGeneratedWorkspace = MigrateLegacyGeneratedWorkspace(
                profile,
                existingGeneratedWorkspace,
                progress);
        }

        var generatedWorkspacePath = existingGeneratedWorkspace ??
                                     Path.Combine(preferredRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        var workspacePath = string.IsNullOrWhiteSpace(profile.WorkspacePath) ||
                            ShouldRefreshUnusedGeneratedPath(profile, managedRoots)
            ? generatedWorkspacePath
            : profile.WorkspacePath;

        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) && existingGeneratedWorkspace is not null)
        {
            progress?.Report(LocalizedText.Format(Strings.Workspace_ExistingFolderFormat, existingGeneratedWorkspace));
        }

        var hasCustomWorkspaceMarker = File.Exists(Path.Combine(workspacePath, MarkerFileName)) &&
                                       HasManagedParentMarker(workspacePath);
        if (!hasCustomWorkspaceMarker &&
            !string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
            !string.IsNullOrWhiteSpace(gamePath) &&
            !FileSystemSafety.IsSameDirectory(Path.GetPathRoot(workspacePath)!, Path.GetPathRoot(preferredRoot)!))
        {
            workspacePath = Path.Combine(preferredRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        }

        if (!hasCustomWorkspaceMarker && !managedRoots.Any(root =>
                FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                !FileSystemSafety.IsSameDirectory(workspacePath, root)))
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_WorkspaceProfilePathFormat,
                workspacePath));
        }

        ValidateProfileLocation(profile, workspacePath, gamePath);
        var workspaceAlreadyExisted = Directory.Exists(workspacePath);
        var workspaceManagedRoot = managedRoots.FirstOrDefault(root =>
                                       FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                                       !FileSystemSafety.IsSameDirectory(workspacePath, root)) ??
                                   Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName ??
                                   preferredRoot;
        ValidateProfileLocation(profile, workspaceManagedRoot, gamePath);
        var workspaceRootAlreadyExisted = Directory.Exists(workspaceManagedRoot);
        Directory.CreateDirectory(workspacePath);
        if (EnsureWorkspaceRootMarker(workspaceManagedRoot) && workspaceRootAlreadyExisted)
        {
            progress?.Report(Strings.Workspace_RootMarkerRestored);
        }

        var markerPath = Path.Combine(workspacePath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            WriteWorkspaceMarker(markerPath);
            if (workspaceAlreadyExisted)
            {
                progress?.Report(Strings.Workspace_ProfileMarkerRebuildRestored);
            }
        }

        return workspacePath;
    }

    internal static void ValidateProfileLocation(ModProfile profile, string workspacePath, string gamePath)
    {
        var sources = new[] { gamePath, profile.Mo2OverwritePath }.Concat(profile.Mods.Select(mod => mod.SourcePath));
        if (sources.Where(path => !string.IsNullOrWhiteSpace(path)).Any(source =>
                FileSystemSafety.IsDirectoryInside(workspacePath, source) ||
                FileSystemSafety.IsDirectoryInside(source, workspacePath)))
            throw new InvalidOperationException(Strings.ProfileStorage_SourceOverlap);
    }

    private string? FindAllowedWorkspaceParent(string workspacePath, string gamePath)
    {
        var managedRoot = _paths.GetManagedWorkspaceRoots(gamePath)
            .Select(Path.GetFullPath)
            .FirstOrDefault(root =>
                FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                !FileSystemSafety.IsSameDirectory(workspacePath, root));
        if (managedRoot is not null)
        {
            return managedRoot;
        }

        if (!File.Exists(Path.Combine(workspacePath, MarkerFileName)) || !HasManagedParentMarker(workspacePath))
        {
            return null;
        }

        return Directory.GetParent(workspacePath)?.FullName;
    }

    private static bool HasManagedParentMarker(string workspacePath)
    {
        var parent = Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName;
        return parent is not null && !FileSystemSafety.IsFileSystemRoot(parent) &&
               File.Exists(Path.Combine(parent, RootMarkerFileName));
    }

    private static bool RestoreGeneratedWorkspaceMarker(
        ModProfile profile,
        string workspacePath,
        string managedRoot)
    {
        if (!IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath))
        {
            return false;
        }

        Directory.CreateDirectory(managedRoot);
        var rootMarkerRestored = EnsureWorkspaceRootMarker(managedRoot);
        Directory.CreateDirectory(workspacePath);
        var markerPath = Path.Combine(workspacePath, MarkerFileName);
        var profileMarkerRestored = false;
        if (!File.Exists(markerPath))
        {
            WriteWorkspaceMarker(markerPath);
            profileMarkerRestored = true;
        }

        return rootMarkerRestored || profileMarkerRestored;
    }

    private static bool IsGeneratedWorkspaceOwnedByProfile(ModProfile profile, string workspacePath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath));
        var shortId = profile.Id.Length > 8 ? profile.Id[..8] : profile.Id;
        return directoryName.Equals(
                   ProfileManager.CreateWorkspaceDirectoryName(profile),
                   StringComparison.OrdinalIgnoreCase) ||
               directoryName.EndsWith($"-{shortId}", StringComparison.OrdinalIgnoreCase) ||
               directoryName.EndsWith($"-{profile.Id}", StringComparison.OrdinalIgnoreCase);
    }

    private static string MigrateLegacyGeneratedWorkspace(
        ModProfile profile,
        string workspacePath,
        IProgress<string>? progress)
    {
        var destinationName = ProfileManager.CreateWorkspaceDirectoryName(profile);
        var currentName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath));
        if (currentName.Equals(destinationName, StringComparison.OrdinalIgnoreCase))
        {
            return workspacePath;
        }

        var parent = Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName ??
                     throw new IOException(LocalizedText.Format(Strings.Error_WorkspaceParentMissingFormat, workspacePath));
        var destination = Path.Combine(parent, destinationName);
        if (Directory.Exists(destination))
        {
            throw new IOException(LocalizedText.Format(
                Strings.Error_WorkspaceLegacyDestinationExistsFormat,
                destination));
        }

        LegacyProfileDataAlias.Delete(workspacePath, profile.Id);
        Directory.Move(workspacePath, destination);
        progress?.Report(LocalizedText.Format(Strings.Workspace_MovedToAsciiFormat, destination));
        return destination;
    }

    private List<string> FindGeneratedWorkspacePaths(ModProfile profile, string gamePath)
    {
        var matches = new List<string>();
        foreach (var managedRoot in _paths.GetManagedWorkspaceRoots(gamePath)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(managedRoot))
            {
                continue;
            }

            try
            {
                matches.AddRange(Directory.EnumerateDirectories(managedRoot, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .Where(path => IsGeneratedWorkspaceOwnedByProfile(profile, path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An inaccessible optional workspace root must not hide valid roots.
            }
        }

        return matches;
    }

    private static bool EnsureWorkspaceRootMarker(string workspaceRoot)
    {
        if (FileSystemSafety.IsFileSystemRoot(workspaceRoot))
            throw new InvalidOperationException(Strings.Workspace_DriveRootForbidden);
        Directory.CreateDirectory(workspaceRoot);
        var rootMarker = Path.Combine(workspaceRoot, RootMarkerFileName);
        if (!File.Exists(rootMarker))
        {
            File.WriteAllText(rootMarker, "Managed workspace root created by CORDON.");
            return true;
        }

        return false;
    }

    private static void WriteWorkspaceMarker(string markerPath)
    {
        File.WriteAllText(markerPath, "Managed by CORDON. It is safe for the launcher to recreate the 'current' subfolder.");
    }

    private static bool ShouldRefreshUnusedGeneratedPath(ModProfile profile, IReadOnlyList<string> managedRoots)
    {
        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) || Directory.Exists(profile.WorkspacePath))
        {
            return false;
        }

        var workspacePath = Path.GetFullPath(profile.WorkspacePath);
        if (!managedRoots.Any(root => FileSystemSafety.IsDirectoryInside(workspacePath, root)))
        {
            return false;
        }

        return IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath);
    }

}
