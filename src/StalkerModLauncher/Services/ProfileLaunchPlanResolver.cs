using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed record LaunchExecutableResolution(
    string FullPath,
    string RelativePath,
    string SourceName,
    string Reason,
    bool UsedRequestedRelativePath,
    bool IsPinned,
    bool IsAvailable = true);

internal sealed record LaunchPlanResolution(
    LaunchPlan? Plan,
    LaunchExecutableResolution? Executable,
    string? Error)
{
    public bool IsReady => Plan is not null && Executable is { IsAvailable: true } && string.IsNullOrWhiteSpace(Error);
}

internal static class ProfileLaunchPlanResolver
{
    public static LaunchPlan CreatePreparedPlan(
        LaunchBackendKind backendKind,
        ModProfile profile,
        WorkspaceBuildResult workspace)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(workspace.WorkingDirectoryRelative)
            ? workspace.WorkspaceRoot
            : FileSystemSafety.ResolvePathInside(
                workspace.WorkspaceRoot,
                workspace.WorkingDirectoryRelative,
                Strings.Safety_UsvfsWorkingDirectory);

        return new LaunchPlan(
            backendKind,
            workspace.ExecutablePath,
            profile.LaunchArguments,
            workingDirectory);
    }

    public static LaunchPlanResolution PreviewLinkedWorkspace(
        ModProfile profile,
        FileLayerPlan fileLayerPlan,
        string profileWorkspace)
    {
        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Safety_LaunchExecutable);
            var executable = ResolveExecutableSource(
                profile,
                FileLayerSourceResolver.CreateExecutableRoots(fileLayerPlan),
                profile.ExecutableRelativePath,
                allowPinnedSource: true,
                allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(profile.ExecutableRelativePath));

            if (executable is null)
            {
                return new LaunchPlanResolution(
                    null,
                    null,
                    LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, profile.ExecutableRelativePath));
            }

            if (!executable.IsAvailable)
            {
                return new LaunchPlanResolution(null, executable, executable.Reason);
            }

            var currentWorkspace = Path.Combine(profileWorkspace, "current");
            var executablePath = FileSystemSafety.ResolvePathInside(
                currentWorkspace,
                executable.RelativePath,
                Strings.Safety_LaunchExecutable);
            var workingDirectoryRelative = FindWorkingDirectoryRelative(fileLayerPlan);
            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryRelative)
                ? currentWorkspace
                : FileSystemSafety.ResolvePathInside(
                    currentWorkspace,
                    workingDirectoryRelative,
                    Strings.Safety_UsvfsWorkingDirectory);

            return new LaunchPlanResolution(
                new LaunchPlan(
                    LaunchBackendKind.LinkedWorkspace,
                    executablePath,
                    profile.LaunchArguments,
                    workingDirectory),
                executable,
                null);
        }
        catch (Exception ex)
        {
            return new LaunchPlanResolution(null, null, ex.Message);
        }
    }

    public static LaunchPlanResolution PreviewStandalone(ModProfile profile, CancellationToken cancellationToken = default)
    {
        var modRoot = profile.Mods.FirstOrDefault(mod => mod.IsEnabled && Directory.Exists(mod.SourcePath))?.SourcePath;
        if (modRoot is null)
        {
            return new LaunchPlanResolution(null, null, Strings.Error_StandaloneNoValidMod);
        }

        modRoot = Path.GetFullPath(modRoot);
        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Safety_LaunchExecutable);
            var executable = ResolveExecutableSource(
                profile,
            [new LaunchExecutableSearchRoot(modRoot, Strings.Profile_Standalone, 1)],
                profile.ExecutableRelativePath,
                allowPinnedSource: false,
                allowDedicatedFallback: false,
                cancellationToken);

            if (executable is null)
            {
                return new LaunchPlanResolution(
                    null,
                    null,
                    LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, profile.ExecutableRelativePath));
            }

            if (!executable.IsAvailable)
            {
                return new LaunchPlanResolution(null, executable, executable.Reason);
            }

            var workingDirectoryRelative = FindWorkingDirectoryRelative(modRoot, profile.WorkingDirectoryRelative);
            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryRelative)
                ? modRoot
                : FileSystemSafety.ResolvePathInside(
                    modRoot,
                    workingDirectoryRelative,
                    Strings.Safety_UsvfsWorkingDirectory);

            return new LaunchPlanResolution(
                new LaunchPlan(
                    LaunchBackendKind.LinkedWorkspace,
                    executable.FullPath,
                    profile.LaunchArguments,
                    workingDirectory),
                executable,
                null);
        }
        catch (Exception ex)
        {
            return new LaunchPlanResolution(null, null, ex.Message);
        }
    }

    public static LaunchPlanResolution PreviewVirtualFileSystem(ModProfile profile, FileLayerPlan fileLayerPlan)
    {
        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Safety_LaunchExecutable);
            var executable = ResolveExecutableSource(
                profile,
                FileLayerSourceResolver.CreateExecutableRoots(fileLayerPlan),
                profile.ExecutableRelativePath,
                allowPinnedSource: true,
                allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(profile.ExecutableRelativePath));

            if (executable is null)
            {
                return new LaunchPlanResolution(
                    null,
                    null,
                    LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, profile.ExecutableRelativePath));
            }

            if (!executable.IsAvailable)
            {
                return new LaunchPlanResolution(null, executable, executable.Reason);
            }

            var virtualRoot = Path.GetFullPath(fileLayerPlan.BaseGame.RootPath);
            var workingDirectoryRelative = FindWorkingDirectoryRelative(fileLayerPlan);
            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryRelative)
                ? virtualRoot
                : FileSystemSafety.ResolvePathInside(
                    virtualRoot,
                    workingDirectoryRelative,
                    Strings.Safety_UsvfsWorkingDirectory);

            return new LaunchPlanResolution(
                new LaunchPlan(
                    LaunchBackendKind.VirtualFileSystem,
                    executable.FullPath,
                    profile.LaunchArguments,
                    workingDirectory),
                executable,
                null);
        }
        catch (Exception ex)
        {
            return new LaunchPlanResolution(null, null, ex.Message);
        }
    }

    public static LaunchExecutableResolution? ResolveExecutableSource(
        ModProfile profile,
        IReadOnlyList<LaunchExecutableSearchRoot> roots,
        string requestedRelativePath,
        bool allowPinnedSource,
        bool allowDedicatedFallback,
        CancellationToken cancellationToken = default)
    {
        if (allowPinnedSource &&
            !profile.IsStandalone &&
            !string.IsNullOrWhiteSpace(profile.ExecutableSourcePath))
        {
            return ResolvePinnedExecutable(profile, requestedRelativePath);
        }

        var exact = roots
            .Where(root => Directory.Exists(root.RootPath))
            .Select(root => new
            {
                FullPath = Path.Combine(root.RootPath, requestedRelativePath),
                root.DisplayName,
                root.Order
            })
            .Where(candidate => File.Exists(candidate.FullPath))
            .OrderByDescending(candidate => candidate.Order)
            .FirstOrDefault();
        if (exact is not null)
        {
            return new LaunchExecutableResolution(
                exact.FullPath,
                requestedRelativePath,
                exact.DisplayName,
                Strings.LaunchPlan_SelectedPathFound,
                UsedRequestedRelativePath: true,
                IsPinned: false);
        }

        var detected = LaunchExecutableDetector.DetectBest(
            roots,
            requestedRelativePath,
            allowDedicatedFallback,
            cancellationToken);
        if (detected is null || detected.Score > 50 && detected.CandidateCount != 1)
        {
            return null;
        }

        return new LaunchExecutableResolution(
            detected.FullPath,
            detected.RelativePath,
            detected.SourceName,
            detected.Reason,
            UsedRequestedRelativePath: false,
            IsPinned: false);
    }

    private static LaunchExecutableResolution ResolvePinnedExecutable(ModProfile profile, string requestedRelativePath)
    {
        var pinnedSource = ProfileExecutableSourceResolver.FindPinnedSourceRoot(profile);
        if (pinnedSource is null)
        {
            return new LaunchExecutableResolution(
                profile.ExecutableSourcePath,
                requestedRelativePath,
                Strings.LaunchPlan_ManualSource,
                Strings.LaunchPlan_ManualSourceUnavailable,
                UsedRequestedRelativePath: true,
                IsPinned: true,
                IsAvailable: false);
        }

        var pinnedExecutable = FileSystemSafety.ResolvePathInside(
            pinnedSource.RootPath,
            requestedRelativePath,
            Strings.Safety_LaunchExecutable);

        return File.Exists(pinnedExecutable)
            ? new LaunchExecutableResolution(
                pinnedExecutable,
                requestedRelativePath,
                pinnedSource.DisplayName,
                Strings.LaunchPlan_SelectedManually,
                UsedRequestedRelativePath: true,
                IsPinned: true)
            : new LaunchExecutableResolution(
                pinnedExecutable,
                requestedRelativePath,
                pinnedSource.DisplayName,
            Strings.LaunchPlan_ManualFileMissing,
                UsedRequestedRelativePath: true,
                IsPinned: true,
                IsAvailable: false);
    }

    private static string FindWorkingDirectoryRelative(FileLayerPlan plan)
    {
        if (plan.UsesFsgameLaunchArgument)
        {
            return string.Empty;
        }

        if (plan.ManualFsgameSource is { } manualSource)
        {
            return Path.GetDirectoryName(manualSource.RelativePath) ?? string.Empty;
        }

        string? result = null;
        foreach (var layer in plan.SourceLayers.Where(layer => Directory.Exists(layer.RootPath)))
        {
            var directory = ProfileDataConfigurator.FindFileDirectory(layer.RootPath, "fsgame.ltx");
            if (directory is null)
            {
                continue;
            }

            var relative = Path.GetRelativePath(layer.RootPath, directory);
            result = relative == "." ? string.Empty : relative;
        }

        return result ?? string.Empty;
    }

    private static string FindWorkingDirectoryRelative(string root, string currentRelative)
    {
        var fsgameDir = ProfileDataConfigurator.FindFileDirectory(root, "fsgame.ltx");
        if (fsgameDir is null)
        {
            return currentRelative;
        }

        var relative = Path.GetRelativePath(root, fsgameDir);
        return relative == "." ? string.Empty : relative;
    }
}
