using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class LaunchPreflightService
{
    private const long LowDiskSpaceBytes = 512L * 1024 * 1024;
    private static readonly string[] EngineDllNames = ["xrCore.dll", "xrGame.dll", "xrEngine.dll"];
    private readonly ProfileManager _profileManager;
    private readonly string _usvfsRuntimeDirectory;

    public LaunchPreflightService(
        ProfileManager profileManager,
        string? usvfsRuntimeDirectory = null)
    {
        _profileManager = profileManager;
        _usvfsRuntimeDirectory = Path.GetFullPath(usvfsRuntimeDirectory ?? AppContext.BaseDirectory);
    }

    public Task<LaunchPreflightReport> AnalyzeAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Analyze(profile, cancellationToken), cancellationToken);
    }

    private LaunchPreflightReport Analyze(ModProfile profile, CancellationToken cancellationToken)
    {
        var checks = new List<ProfileHealthCheck>();
        FileLayerPlan? fileLayerPlan = null;
        try
        {
            fileLayerPlan = TryCreateLinkedFileLayerPlan(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            checks.Add(Error(Strings.Check_ProfilePreparation, ex.Message));
        }
        var launchPlan = TryCreateLaunchPlan(profile, fileLayerPlan, cancellationToken);
        var overlayManifest = TryCreateOverlayManifest(profile, fileLayerPlan, cancellationToken);
        if (!profile.IsEnabled)
        {
            checks.Add(Error(Strings.Preflight_Profile, Strings.Preflight_ProfileDisabled));
        }

        if (!profile.IsStandalone)
        {
            try
            {
                var dataRoot = ProfileDataPathResolver.GetGameDataRoot(profile);
                checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Healthy, Strings.Check_GameData,
                    profile.UseBaseGameData ? LocalizedText.Format(Strings.Preflight_SharedGameDataFormat, dataRoot) : Strings.Preflight_InProfileFolder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                checks.Add(Error(Strings.Check_GameData, ex.Message));
            }
            var game = GameInstallationValidator.Validate(profile.GameInstallPath);
            checks.Add(new ProfileHealthCheck(
                game.IsValid ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Error,
                Strings.Check_BaseGame,
                game.IsValid ? profile.GameInstallPath : $"{game.Summary} {string.Join(" ", game.Messages)}".Trim()));
        }

        var enabledMods = profile.Mods.Where(mod => mod.IsEnabled).OrderBy(mod => mod.Order).ToArray();
        if (profile.IsStandalone && enabledMods.Length != 1)
        {
            checks.Add(Error(Strings.Profile_Standalone, Strings.Ready_StandaloneModCount));
        }

        foreach (var mod in enabledMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = Directory.Exists(mod.SourcePath);
            checks.Add(new ProfileHealthCheck(
                exists ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Error,
                LocalizedText.Format(Strings.Preflight_SourceFormat, mod.Name),
                exists ? mod.SourcePath : LocalizedText.Format(Strings.Ready_ModFolderMissingFormat, mod.SourcePath)));

            if (exists && !HasAnyFile(mod.SourcePath, cancellationToken))
            {
                checks.Add(new ProfileHealthCheck(
                    ProfileHealthStatus.Warning,
                    LocalizedText.Format(Strings.Preflight_EmptyModTitleFormat, mod.Name),
                    Strings.Preflight_EmptyMod));
            }
        }

        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Check_LaunchBinary);
            var executableSource = launchPlan?.Executable ??
                                   FindFinalExecutableSource(profile, profile.ExecutableRelativePath, fileLayerPlan, cancellationToken);
            checks.Add(new ProfileHealthCheck(
                executableSource is null || !executableSource.IsAvailable
                    ? ProfileHealthStatus.Error
                    : executableSource.UsedRequestedRelativePath ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
                Strings.Preflight_FinalExecutable,
                executableSource is null
                    ? LocalizedText.Format(Strings.Preflight_FinalExecutableMissingFormat, profile.ExecutableRelativePath)
                    : FormatExecutableSource(executableSource, profile.ExecutableRelativePath)));

            if (executableSource is { IsAvailable: true })
            {
                AddCompanionDllCheck(checks, profile, fileLayerPlan, executableSource.FullPath, executableSource.RelativePath);
            }
        }
        catch (Exception ex)
        {
            checks.Add(Error(Strings.Check_LaunchBinary, ex.Message));
        }

        try
        {
            var fsgameSource = FindFinalSource(profile, "fsgame.ltx", fileLayerPlan);
            if (!profile.IsStandalone && fsgameSource is null)
            {
                throw new FileNotFoundException(Strings.Ready_FsgameMissing);
            }

            if (!profile.IsStandalone)
            {
                ProfileDataConfigurator.ValidateFsgameSource(fsgameSource!);
            }

            checks.Add(new ProfileHealthCheck(
                fsgameSource is null ? ProfileHealthStatus.Warning : ProfileHealthStatus.Healthy,
                "fsgame.ltx",
                fsgameSource ?? Strings.Preflight_FsgameOptional));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            checks.Add(Error("fsgame.ltx", ex.Message));
        }

        AddUsvfsRuntimeCheck(checks, profile, fileLayerPlan, launchPlan);

        if (!profile.IsStandalone)
        {
            AddWorkspaceChecks(checks, profile);
            AddLinkSupportCheck(checks, profile, fileLayerPlan, enabledMods, cancellationToken);
        }

        return new LaunchPreflightReport(checks, launchPlan?.Plan, overlayManifest);
    }

    private LaunchPlanResolution? TryCreateLaunchPlan(
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        CancellationToken cancellationToken)
    {
        if (profile.IsStandalone)
        {
            return ProfileLaunchPlanResolver.PreviewStandalone(profile, cancellationToken);
        }

        if (fileLayerPlan is null)
        {
            return null;
        }

        var workspace = _profileManager.GetProfileFolderPath(profile);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return null;
        }

        return profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
            ? ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, fileLayerPlan)
            : ProfileLaunchPlanResolver.PreviewLinkedWorkspace(profile, fileLayerPlan, workspace);
    }

    private OverlayManifest? TryCreateOverlayManifest(
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        CancellationToken cancellationToken)
    {
        if (fileLayerPlan is null)
        {
            return null;
        }

        var workspace = _profileManager.GetProfileFolderPath(profile);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return null;
        }

        return profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
            ? OverlayManifestBuilder.BuildVirtualFileSystem(
                profile,
                fileLayerPlan,
                workspace,
                cancellationToken: cancellationToken)
            : OverlayManifestBuilder.BuildLinkedWorkspace(
                profile,
                fileLayerPlan,
                workspace,
                cancellationToken: cancellationToken);
    }

    private FileLayerPlan? TryCreateLinkedFileLayerPlan(ModProfile profile)
    {
        if (profile.IsStandalone || string.IsNullOrWhiteSpace(profile.GameInstallPath))
        {
            return null;
        }

        var workspace = _profileManager.GetProfileFolderPath(profile);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return null;
        }

        return FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, workspace);
    }

    private void AddWorkspaceChecks(List<ProfileHealthCheck> checks, ModProfile profile)
    {
        var workspace = _profileManager.GetProfileFolderPath(profile);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            checks.Add(Error("Workspace", Strings.Preflight_WorkspacePathMissing));
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(workspace));
            var drive = root is null ? null : new DriveInfo(root);
            checks.Add(new ProfileHealthCheck(
                drive is { AvailableFreeSpace: < LowDiskSpaceBytes } ? ProfileHealthStatus.Warning : ProfileHealthStatus.Healthy,
                Strings.Preflight_FreeSpace,
                drive is null ? Strings.Preflight_WorkspaceDriveMissing : LocalizedText.Format(Strings.Preflight_FreeSpaceFormat, WorkspaceStatus.FormatSize(drive.AvailableFreeSpace), drive.Name)));
        }
        catch (Exception ex)
        {
            checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Warning, Strings.Preflight_FreeSpace, ex.Message));
        }
    }

    private void AddUsvfsRuntimeCheck(
        List<ProfileHealthCheck> checks,
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        LaunchPlanResolution? launchResolution)
    {
        if (profile.LaunchBackendKind != LaunchBackendKind.VirtualFileSystem)
        {
            return;
        }

        if (profile.IsStandalone)
        {
            checks.Add(Error("USVFS", Strings.Preflight_UsvfsLayeredOnly));
            return;
        }

        var runtimeFiles = UsvfsRuntimeFiles.Check(_usvfsRuntimeDirectory);
        if (!runtimeFiles.IsReady)
        {
            checks.Add(Error(
                "USVFS runtime",
                runtimeFiles.MissingFilesMessage(WindowsExecutableArchitecture.Unknown)));
            return;
        }

        if (fileLayerPlan is null || launchResolution is null || !launchResolution.IsReady)
        {
            return;
        }

        try
        {
            var target = AnomalyUsvfsLaunchTargetResolver.Resolve(profile, fileLayerPlan, launchResolution);
            var architecture = WindowsExecutableArchitectureDetector.Detect(target.ExecutablePath);
            if (architecture == WindowsExecutableArchitecture.Unknown)
            {
                checks.Add(Error(
                    "USVFS runtime",
                    LocalizedText.Format(Strings.Preflight_ArchitectureMissingFormat, target.ExecutablePath)));
                return;
            }

            checks.Add(new ProfileHealthCheck(
                ProfileHealthStatus.Healthy,
                "USVFS runtime",
                LocalizedText.Format(Strings.Preflight_UsvfsReadyFormat, runtimeFiles.RuntimeVersion, FormatArchitecture(architecture), target.ExecutablePath)));
        }
        catch (Exception ex)
        {
            checks.Add(Error("USVFS runtime", ex.Message));
        }
    }

    private void AddLinkSupportCheck(
        List<ProfileHealthCheck> checks,
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        IReadOnlyList<ModEntry> enabledMods,
        CancellationToken cancellationToken)
    {
        var workspace = _profileManager.GetProfileFolderPath(profile);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return;
        }

        try
        {
            var workspaceVolume = Path.GetPathRoot(Path.GetFullPath(workspace));
            var sourcePaths = fileLayerPlan is null
                ? enabledMods.Select(mod => mod.SourcePath)
                : fileLayerPlan.Mods.Select(layer => layer.RootPath);
            var source = sourcePaths
                .Where(Directory.Exists)
                .Where(path => !string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), workspaceVolume, StringComparison.OrdinalIgnoreCase))
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(1))
                .FirstOrDefault();
            if (source is null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workspace);
            var testLink = Path.Combine(workspace, $".preflight-link-{Guid.NewGuid():N}");
            try
            {
                File.CreateSymbolicLink(testLink, source);
                checks.Add(new ProfileHealthCheck(
                    ProfileHealthStatus.Healthy,
                    Strings.Preflight_CrossDriveLinks,
                    LocalizedText.Format(Strings.Preflight_CrossDriveAllowedFormat, Path.GetPathRoot(source))));
            }
            finally
            {
                File.Delete(testLink);
            }
        }
        catch (Exception ex)
        {
            checks.Add(Error(
                Strings.Preflight_CrossDriveLinks,
                LocalizedText.Format(Strings.Preflight_CrossDriveDeniedFormat, ex.Message)));
        }
    }

    private static void AddCompanionDllCheck(
        List<ProfileHealthCheck> checks,
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        string executableSource,
        string executableRelativePath)
    {
        var executableName = Path.GetFileName(executableSource);
        if (!executableName.Contains("xr", StringComparison.OrdinalIgnoreCase) &&
            !executableName.Contains("ogsr", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relativeDirectory = Path.GetDirectoryName(executableRelativePath) ?? string.Empty;
        var hasEngineDll = EngineDllNames
            .Any(name => FindFinalSource(profile, Path.Combine(relativeDirectory, name), fileLayerPlan) is not null);
        checks.Add(new ProfileHealthCheck(
            hasEngineDll ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
            Strings.Preflight_EngineFiles,
            hasEngineDll
                ? Strings.Preflight_EngineDllFound
                : Strings.Preflight_EngineDllMissing));
    }

    private static bool HasAnyFile(string root, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var _ in Directory.EnumerateFiles(root, "*", SafeEnumerationOptions).Take(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }

        return false;
    }

    internal static string? FindFinalSource(ModProfile profile, string relativePath, FileLayerPlan? fileLayerPlan = null)
    {
        if (fileLayerPlan is not null)
        {
            if (relativePath.Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase))
            {
                return fileLayerPlan.FindFsgameSource()?.FullPath;
            }

            return FileLayerSourceResolver.FindFinalSource(fileLayerPlan, relativePath)?.FullPath;
        }

        if (!profile.IsStandalone && relativePath.Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase))
        {
            return FileLayerPlan.ResolveFsgameSource(profile)?.FullPath;
        }

        var roots = new List<string>();
        if (!profile.IsStandalone)
        {
            roots.Add(profile.GameInstallPath);
        }

        roots.AddRange(profile.Mods
            .Where(mod => mod.IsEnabled)
            .OrderBy(mod => mod.Order)
            .Select(mod => mod.SourcePath));

        return roots
            .Where(Directory.Exists)
            .Select(root => Path.Combine(root, relativePath))
            .LastOrDefault(File.Exists);
    }

    private static LaunchExecutableResolution? FindFinalExecutableSource(
        ModProfile profile,
        string requestedRelativePath,
        FileLayerPlan? fileLayerPlan,
        CancellationToken cancellationToken)
    {
        var roots = fileLayerPlan is null
            ? CreateExecutableRoots(profile).ToArray()
            : FileLayerSourceResolver.CreateExecutableRoots(fileLayerPlan);
        return ProfileLaunchPlanResolver.ResolveExecutableSource(
            profile,
            roots,
            requestedRelativePath,
            allowPinnedSource: true,
            allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(requestedRelativePath),
            cancellationToken);
    }

    private static IEnumerable<LaunchExecutableSearchRoot> CreateExecutableRoots(ModProfile profile)
    {
        if (!profile.IsStandalone)
        {
            yield return new LaunchExecutableSearchRoot(profile.GameInstallPath, Strings.Layer_BaseGame, 0);
        }

        foreach (var mod in profile.Mods
                     .Where(mod => mod.IsEnabled)
                     .OrderBy(mod => mod.Order))
        {
            yield return new LaunchExecutableSearchRoot(mod.SourcePath, LocalizedText.Format(Strings.Layer_ModFormat, mod.Name), mod.Order);
        }
    }

    private static string FormatExecutableSource(LaunchExecutableResolution source, string requestedRelativePath)
    {
        if (!source.IsAvailable)
        {
            return LocalizedText.Format(Strings.Check_ManualSourceMissingFormat, source.FullPath, " ", source.Reason);
        }

        if (source.IsPinned)
        {
            return LocalizedText.Format(Strings.Check_FinalManualFormat, source.FullPath, ". ", source.SourceName);
        }

        if (source.UsedRequestedRelativePath)
        {
            return LocalizedText.Format(Strings.Check_FinalAutomaticFormat, source.FullPath, ". ", source.SourceName);
        }

        return LocalizedText.Format(
            Strings.Check_FinalFallbackFormat,
            requestedRelativePath,
            " ",
            source.FullPath,
            source.Reason,
            source.SourceName);
    }

    private static ProfileHealthCheck Error(string title, string details) =>
        new(ProfileHealthStatus.Error, title, details);

    private static string FormatArchitecture(WindowsExecutableArchitecture architecture) =>
        architecture == WindowsExecutableArchitecture.X86 ? "x86" : "x64";

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

}
