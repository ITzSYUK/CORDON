using System.Diagnostics;
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
    private const string ManifestFileName = "build-manifest.json";
    private const string WorkspaceFormatVersion = "strict-links-v5";
    private readonly AppPaths _paths;
    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public WorkspaceBuilder(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<WorkspaceBuildResult> BuildAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        FileLayerPlan? fileLayerPlan = null,
        OverlayManifest? overlayManifest = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Build(gamePath, profile, progress, fileLayerPlan, overlayManifest, cancellationToken), cancellationToken);
    }

    private WorkspaceBuildResult Build(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        FileLayerPlan? providedFileLayerPlan,
        OverlayManifest? providedOverlayManifest,
        CancellationToken cancellationToken)
    {
        FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Safety_LaunchExecutable);

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            throw new InvalidOperationException(Strings.Workspace_GameFolderRequired);
        }

        if (!Directory.Exists(gamePath))
        {
            throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_GameFolderNotFoundFormat, gamePath));
        }

        var totalTimer = Stopwatch.StartNew();
        var workspaceRoot = EnsureProfileWorkspace(profile, gamePath, progress);
        var currentWorkspace = Path.Combine(workspaceRoot, "current");
        FileSystemSafety.EnsureDirectoryInside(currentWorkspace, workspaceRoot);
        FileSystemSafety.EnsureNoDirectoryLinks(currentWorkspace);
        var fileLayerPlan = providedFileLayerPlan ?? FileLayerPlan.CreateForProfile(gamePath, profile, workspaceRoot);
        progress.Report(Strings.Workspace_CheckingFiles);
        var scanTimer = Stopwatch.StartNew();
        var overlayManifest = providedOverlayManifest ?? OverlayManifestBuilder.BuildLinkedWorkspace(
            profile, fileLayerPlan, workspaceRoot, cancellationToken: cancellationToken);
        var writableFiles = overlayManifest.WritableFiles;
        var trackedPaths = overlayManifest.UsesMiseryClasses ? writableFiles.Select(file => file.RelativePath) : null;
        var sourceSnapshot = WorkspaceSourceScanner.Capture(fileLayerPlan, cancellationToken);
        var formatVersion = fileLayerPlan.UsesAnomalyAppDataAlias ? WorkspaceFormatVersion + "-anomaly-appdata-v1" : WorkspaceFormatVersion;
        if (overlayManifest.UsesMiseryClasses)
            formatVersion += "-misery-classes-v1";
        var buildFingerprint = WorkspaceSourceScanner.CreateBuildFingerprint(formatVersion, profile, sourceSnapshot, fileLayerPlan);
        scanTimer.Stop();
        ProfileWritableGameFileStore.Prepare(fileLayerPlan, overlayManifest, workspaceRoot, progress,
            linkedWorkspace: true, cancellationToken: cancellationToken);
        var cachedExecutable = WorkspaceManifestStore.TryGetCachedExecutable(
            workspaceRoot,
            currentWorkspace,
            profile,
            buildFingerprint,
            fileLayerPlan,
            progress,
            cancellationToken);
        if (cachedExecutable is not null)
        {
            ProfileWritableGameFileStore.RestoreToWorkspace(currentWorkspace, writableFiles, progress);
            var cachedWorkingDirectoryRelative = ProfileDataConfigurator.Configure(
                gamePath,
                currentWorkspace,
                workspaceRoot,
                progress,
                fileLayerPlan,
                overlayManifest.UsesMiseryClasses,
                cancellationToken);
            ProfileWritableGameFileStore.RecordBootstrapState(currentWorkspace, trackedPaths);
            progress.Report(LocalizedText.Format(Strings.Workspace_ScanCompleteFormat, FormatElapsed(scanTimer.Elapsed)));
            return new WorkspaceBuildResult(
                currentWorkspace,
                cachedExecutable,
                workspaceRoot,
                profile.ExecutableRelativePath,
                cachedWorkingDirectoryRelative);
        }

        WorkspaceMaterializer.ValidateLinkSupport(sourceSnapshot, workspaceRoot, progress);

        progress.Report(Strings.Workspace_Preparing);
        var cleanupTimer = Stopwatch.StartNew();
        if (fileLayerPlan.UsesAnomalyAppDataAlias)
            AnomalyAppDataAlias.ArchiveLegacyUserFiles(workspaceRoot, fileLayerPlan.GameDataRoot, includeCurrent: true);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(workspaceRoot, ManifestFileName));
        WorkspaceMaterializer.DeleteWorkspaceContents(currentWorkspace, workspaceRoot, progress);
        Directory.CreateDirectory(currentWorkspace);
        cleanupTimer.Stop();

        var stats = new WorkspaceBuildStats();

        progress.Report(Strings.Workspace_ConnectingGame);
        var baseGameTimer = Stopwatch.StartNew();
        WorkspaceMaterializer.MirrorBaseGame(sourceSnapshot.Game, currentWorkspace, progress, stats, cancellationToken);
        baseGameTimer.Stop();

        var modsTimer = Stopwatch.StartNew();
        foreach (var layer in fileLayerPlan.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceMaterializer.ApplyMod(currentWorkspace, layer, sourceSnapshot.Mods[layer.Id], progress, stats, cancellationToken);
        }
        modsTimer.Stop();

        var configurationTimer = Stopwatch.StartNew();
        var workingDirectoryRelative = ProfileDataConfigurator.Configure(
            gamePath,
            currentWorkspace,
            workspaceRoot,
            progress,
            fileLayerPlan,
            overlayManifest.UsesMiseryClasses,
            cancellationToken);
        ProfileWritableGameFileStore.RestoreToWorkspace(currentWorkspace, writableFiles, progress, stats);
        ApplyPinnedExecutableSource(profile, currentWorkspace, progress, stats);
        configurationTimer.Stop();

        var executablePath = Path.Combine(currentWorkspace, profile.ExecutableRelativePath);
        var executableRelativePath = profile.ExecutableRelativePath;
        if (!File.Exists(executablePath))
        {
            var detectedExecutable = WorkspaceExecutableResolver.Resolve(currentWorkspace, profile.ExecutableRelativePath, progress);
            if (detectedExecutable is null)
            {
                var discovered = Directory.EnumerateFiles(currentWorkspace, "*.exe", SafeEnumerationOptions)
                    .Select(path => Path.GetRelativePath(currentWorkspace, path))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray();
                var details = discovered.Length == 0
                    ? Strings.Error_NoExecutablesInWorkspace
                    : LocalizedText.Format(Strings.Error_ExecutablesFoundFormat, string.Join(", ", discovered));

                throw new FileNotFoundException(
                    LocalizedText.Format(
                        Strings.Error_ProfileExeAfterBuildFormat,
                        profile.ExecutableRelativePath,
                        details),
                    executablePath);
            }

            executablePath = detectedExecutable.FullPath;
            executableRelativePath = detectedExecutable.RelativePath;
        }

        progress.Report(LocalizedText.Format(Strings.Workspace_ReadyFormat, stats.LinkedFiles, stats.SymbolicLinkedFiles, stats.ProtectedCopies));
        if (stats.ProtectedCopies > 0)
        {
            progress.Report(LocalizedText.Format(Strings.Workspace_LocalFilesFormat, stats.ProtectedCopies, stats.RequiredLocalFiles, stats.ReadOnlyCopiedFiles));
        }

        if (stats.ReadOnlyHandledFiles > 0)
        {
            progress.Report(LocalizedText.Format(Strings.Workspace_ReadOnlyFilesFormat, stats.ReadOnlyHandledFiles, stats.ReadOnlySymbolicLinkedFiles, stats.ReadOnlyCopiedFiles));
        }

        totalTimer.Stop();
        progress.Report(LocalizedText.Format(
            Strings.Workspace_TimingFormat,
            FormatElapsed(scanTimer.Elapsed),
            FormatElapsed(cleanupTimer.Elapsed),
            FormatElapsed(baseGameTimer.Elapsed),
            FormatElapsed(modsTimer.Elapsed),
            FormatElapsed(configurationTimer.Elapsed),
            FormatElapsed(totalTimer.Elapsed)));
        ProfileWritableGameFileStore.RecordBootstrapState(currentWorkspace, trackedPaths);
        WorkspaceManifestStore.Write(workspaceRoot, buildFingerprint, stats);
        return new WorkspaceBuildResult(
            currentWorkspace,
            executablePath,
            workspaceRoot,
            executableRelativePath,
            workingDirectoryRelative);
    }

    private static void ApplyPinnedExecutableSource(
        ModProfile profile,
        string currentWorkspace,
        IProgress<string> progress,
        WorkspaceBuildStats stats)
    {
        var sourceRoot = ProfileExecutableSourceResolver.FindPinnedSourceRoot(profile);
        if (sourceRoot is null)
        {
            if (!string.IsNullOrWhiteSpace(profile.ExecutableSourcePath))
            {
                throw new InvalidOperationException(
                    Strings.Workspace_ManualSourceUnavailable);
            }

            return;
        }

        var sourceFile = FileSystemSafety.ResolvePathInside(
            sourceRoot.RootPath,
            profile.ExecutableRelativePath,
            Strings.Safety_PinnedExecutable);
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException(
                LocalizedText.Format(Strings.Workspace_ManualFileMissingFormat, sourceFile),
                sourceFile);
        }

        WorkspaceMaterializer.ReplaceFile(sourceFile, currentWorkspace, profile.ExecutableRelativePath, stats);
        progress.Report(LocalizedText.Format(Strings.Workspace_ManualExecutableFormat, profile.ExecutableRelativePath, sourceRoot.DisplayName));
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
        var allowedRoot = FindAllowedWorkspaceParent(profile, fullWorkspacePath, gamePath);
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
            allowedRoot);
    }

    public void ClearProfileWorkspaceCache(ModProfile profile, string gamePath)
    {
        ClearProfileWorkspaceCache(profile, gamePath, progress: null);
    }

    public void ClearProfileWorkspaceCache(
        ModProfile profile,
        string gamePath,
        IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) || !Directory.Exists(profile.WorkspacePath))
        {
            return;
        }

        var workspacePath = Path.GetFullPath(profile.WorkspacePath);
        ValidateProfileLocation(profile, workspacePath, gamePath);
        var allowedRoot = FindAllowedWorkspaceParent(profile, workspacePath, gamePath);
        if (allowedRoot is null)
        {
            throw new InvalidOperationException(Strings.Workspace_SafetyMarkerRequired);
        }

        if (RestoreGeneratedWorkspaceMarker(profile, workspacePath, allowedRoot))
        {
            progress?.Report(Strings.Workspace_ProfileMarkerRestored);
        }
        if (!File.Exists(Path.Combine(workspacePath, MarkerFileName)))
        {
            throw new InvalidOperationException(Strings.Workspace_SafetyMarkerRequired);
        }

        var current = Path.Combine(workspacePath, "current");
        if (Directory.Exists(current))
        {
            ProfileWritableGameFileStore.CaptureFromWorkspace(current, workspacePath, progress);
            WorkspaceMaterializer.DeleteWorkspaceContents(current, workspacePath, progress);
        }

        var manifest = Path.Combine(workspacePath, ManifestFileName);
        if (File.Exists(manifest))
        {
            File.Delete(manifest);
        }
    }

    public string EnsureProfileWorkspace(
        ModProfile profile,
        string gamePath,
        IProgress<string>? progress = null)
    {
        var preferredRoot = _paths.GetPreferredWorkspaceRoot(gamePath);
        var managedRoots = _paths.GetManagedWorkspaceRoots(gamePath);

        string workspacePath;
        string? emptyWorkspace = null;
        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath))
        {
            workspacePath = Path.GetFullPath(profile.WorkspacePath);
            if (managedRoots.Any(root => FileSystemSafety.IsSameDirectory(workspacePath, root)))
                throw new InvalidOperationException(LocalizedText.Format(Strings.Error_WorkspaceUnverifiedFormat, workspacePath));
            if (!Directory.Exists(workspacePath))
                throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_WorkspaceUnavailableFormat, workspacePath));
            ValidateProfileLocation(profile, workspacePath, gamePath);
            if (!string.IsNullOrWhiteSpace(profile.Id) && IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath) &&
                !HasDirectoryLinks(workspacePath) && !Directory.EnumerateFileSystemEntries(workspacePath).Any())
            {
                emptyWorkspace = workspacePath;
                workspacePath = Path.Combine(_paths.WorkspaceRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
            }
        }
        else
        {
            var existingGeneratedWorkspace = FindGeneratedWorkspacePaths(profile, gamePath)
                .OrderByDescending(path => File.Exists(Path.Combine(path, MarkerFileName)))
                .ThenByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (existingGeneratedWorkspace is not null)
                ValidateProfileLocation(profile, existingGeneratedWorkspace, gamePath);
            workspacePath = existingGeneratedWorkspace is null
                ? Path.Combine(preferredRoot, ProfileManager.CreateWorkspaceDirectoryName(profile))
                : MigrateLegacyGeneratedWorkspace(profile, existingGeneratedWorkspace, progress);
            if (existingGeneratedWorkspace is not null)
                progress?.Report(LocalizedText.Format(Strings.Workspace_ExistingFolderFormat, workspacePath));
        }

        ValidateProfileLocation(profile, workspacePath, gamePath);
        if (profile.LaunchBackendKind == Models.LaunchBackendKind.VirtualFileSystem)
            _ = UsvfsBootstrapPathResolver.Resolve(workspacePath);
        var hasCustomWorkspaceMarker = File.Exists(Path.Combine(workspacePath, MarkerFileName)) &&
                                       HasManagedParentMarker(workspacePath);
        if (!hasCustomWorkspaceMarker && !managedRoots.Any(root =>
                FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                !FileSystemSafety.IsSameDirectory(workspacePath, root)) &&
            !IsRecoverableLegacyWorkspace(profile, workspacePath))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Error_WorkspaceUnverifiedFormat, workspacePath));
        }

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

        if (emptyWorkspace is not null && !FileSystemSafety.IsSameDirectory(emptyWorkspace, workspacePath))
            progress?.Report(LocalizedText.Format(Strings.Workspace_EmptyRedirectedFormat, emptyWorkspace, workspacePath));
        return workspacePath;
    }

    internal static void ValidateProfileLocation(ModProfile profile, string workspacePath, string gamePath)
    {
        FileSystemSafety.EnsureNoDirectoryLinks(workspacePath);
        var sources = new[] { gamePath, profile.Mo2OverwritePath }.Concat(profile.Mods.Select(mod => mod.SourcePath));
        if (sources.Where(path => !string.IsNullOrWhiteSpace(path)).Any(source =>
                FileSystemSafety.IsDirectoryInside(workspacePath, source) ||
                FileSystemSafety.IsDirectoryInside(source, workspacePath)))
            throw new InvalidOperationException(Strings.ProfileStorage_SourceOverlap);
    }

    private string? FindAllowedWorkspaceParent(ModProfile profile, string workspacePath, string gamePath)
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

        if (!HasManagedParentMarker(workspacePath) ||
            (!File.Exists(Path.Combine(workspacePath, MarkerFileName)) && !IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath)))
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

    internal static bool IsGeneratedWorkspaceOwnedByProfile(ModProfile profile, string workspacePath)
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
        if (profile.LaunchBackendKind == Models.LaunchBackendKind.VirtualFileSystem)
            _ = UsvfsBootstrapPathResolver.Resolve(destination);
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

    internal static bool EnsureWorkspaceRootMarker(string workspaceRoot)
    {
        FileSystemSafety.EnsureNoDirectoryLinks(workspaceRoot);
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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds < 1
            ? LocalizedText.Format(Strings.Workspace_MillisecondsFormat, elapsed.TotalMilliseconds)
            : LocalizedText.Format(Strings.Workspace_SecondsFormat, elapsed.TotalSeconds);
    }

    private static bool IsRecoverableLegacyWorkspace(ModProfile profile, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) return false;
        var parent = Directory.GetParent(workspacePath);
        if (parent is null ||
            (!parent.Name.Equals("Workspace", StringComparison.OrdinalIgnoreCase) &&
             !parent.Name.Equals("Workspaces", StringComparison.OrdinalIgnoreCase)) ||
            (parent.Parent?.Name.Equals("StalkerModLauncher", StringComparison.OrdinalIgnoreCase) != true &&
             parent.Parent?.Name.Equals("CordonModLauncher", StringComparison.OrdinalIgnoreCase) != true) ||
            !IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath))
            return false;

        var userData = new DirectoryInfo(Path.Combine(workspacePath, "userdata"));
        return userData.Exists && !HasDirectoryLinks(userData.FullName);
    }

    internal static bool HasDirectoryLinks(string path)
    {
        // Never authorize future recursive deletion through a link to another directory.
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Attributes != (FileAttributes)(-1) &&
                (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}

public sealed record WorkspaceBuildResult(
    string WorkspaceRoot,
    string ExecutablePath,
    string ProfileWorkspacePath,
    string ExecutableRelativePath,
    string WorkingDirectoryRelative);
