using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class ProfileHealthService
{
    private readonly ProfileManager _profileManager;
    public ProfileHealthService(ProfileManager profileManager)
    {
        _profileManager = profileManager;
    }

    public async Task<ProfileHealthReport> AnalyzeAsync(
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        var report = await Task.Run(() => Analyze(profile, cancellationToken), cancellationToken);
        if (profile.IsStandalone)
        {
            return report;
        }

        var workspace = await WorkspaceManagementService.InspectAsync(profile, cancellationToken);
        return report with { Workspace = workspace };
    }

    private ProfileHealthReport Analyze(ModProfile profile, CancellationToken cancellationToken)
    {
        var checks = new List<ProfileHealthCheck>();
        var gamePath = profile.GameInstallPath;
        var profileFolderPath = _profileManager.GetProfileFolderPath(profile) ?? string.Empty;
        FileLayerPlan? fileLayerPlan = null;
        try
        {
            fileLayerPlan = TryCreateLinkedFileLayerPlan(profile, profileFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Error, Strings.Check_ProfilePreparation, ex.Message));
        }
        var launchPlan = TryCreateLaunchPlan(profile, fileLayerPlan, profileFolderPath, cancellationToken);
        var overlayManifest = TryCreateOverlayManifest(profile, fileLayerPlan, profileFolderPath, cancellationToken);

        if (profile.IsStandalone)
        {
            checks.Add(new ProfileHealthCheck(
                ProfileHealthStatus.Healthy,
                Strings.Check_ProfileMode,
                Strings.Check_StandaloneMode));
        }
        else
        {
            try
            {
                var dataRoot = ProfileDataPathResolver.GetGameDataRoot(profile, profileFolderPath);
                checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Healthy, Strings.Check_GameData,
                    $"{(profile.UseBaseGameData ? Strings.Check_SharedGameData : Strings.Check_ProfileData)}: {dataRoot}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Error, Strings.Check_GameData, ex.Message));
            }
            var validation = GameInstallationValidator.Validate(gamePath);
            checks.Add(new ProfileHealthCheck(
                validation.IsValid ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Error,
                Strings.Check_BaseGame,
                $"{validation.Summary} {string.Join(" ", validation.Messages)}".Trim()));

            AddFsgameSourceCheck(checks, profile, fileLayerPlan);
        }

        foreach (var mod in profile.Mods.OrderBy(mod => mod.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = Directory.Exists(mod.SourcePath);
            checks.Add(new ProfileHealthCheck(
                exists ? ProfileHealthStatus.Healthy : mod.IsEnabled ? ProfileHealthStatus.Error : ProfileHealthStatus.Warning,
                LocalizedText.Format(Strings.Check_ModTitleFormat, mod.Order, mod.Name),
                exists
                    ? LocalizedText.Format(mod.IsEnabled ? Strings.Check_EnabledPathFormat : Strings.Check_DisabledPathFormat, mod.SourcePath)
                    : LocalizedText.Format(mod.IsEnabled ? Strings.Check_EnabledMissingFormat : Strings.Check_DisabledMissingFormat, mod.SourcePath)));
        }

        if (profile.Mods.Count == 0)
        {
            checks.Add(new ProfileHealthCheck(
                profile.IsStandalone ? ProfileHealthStatus.Error : ProfileHealthStatus.Warning,
                Strings.Check_ModList,
                profile.IsStandalone ? Strings.Check_StandaloneNeedsMod : Strings.Check_NoMods));
        }

        var executableSource = launchPlan?.Executable ?? FindExecutableSource(profile, gamePath, fileLayerPlan, cancellationToken);
        checks.Add(new ProfileHealthCheck(
            executableSource is null || !executableSource.IsAvailable
                ? ProfileHealthStatus.Error
                : executableSource.UsedRequestedRelativePath ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
            Strings.Check_LaunchBinary,
            executableSource is null
                ? LocalizedText.Format(Strings.Check_NotFoundFormat, profile.ExecutableRelativePath)
                : FormatExecutableSource(executableSource, profile.ExecutableRelativePath)));

        var savedGamePaths = ProfileDataPathResolver.GetSavedGameDirectories(profile);
        var savedGamesPath = savedGamePaths.FirstOrDefault(Directory.Exists)
            ?? (savedGamePaths.Count > 0 ? savedGamePaths[0] : null)
            ?? string.Empty;

        if (!profile.IsStandalone)
        {
            AddProfileStorageChecks(checks, profile);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var saveCount = CountFiles(
            savedGamesPath,
            cancellationToken,
            ".sav",
            ".scop");
        checks.Add(new ProfileHealthCheck(
            Directory.Exists(savedGamesPath) ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
            Strings.Check_Saves,
            Directory.Exists(savedGamesPath) ? LocalizedText.Format(Strings.Check_SaveCountFormat, saveCount, savedGamesPath) : Strings.Check_SaveFolderMissing));

        var logPaths = ProfileDataPathResolver.GetLogDirectories(profile);
        var latestLog = FindLatest(logPaths, [".log", ".txt"], cancellationToken);
        if (IsUsvfsLog(latestLog))
        {
            latestLog = FindLatest(
                logPaths,
                file => !IsUsvfsLog(file),
                [".log", ".txt"],
                cancellationToken);
        }
        var latestDump = FindLatest(logPaths, [".mdmp", ".dmp"], cancellationToken);
        var usvfsLogPath = UsvfsDiagnosticPaths.Resolve(profileFolderPath);
        checks.Add(new ProfileHealthCheck(
            ProfileHealthStatus.Healthy,
            Strings.Check_LatestLog,
            latestLog is not null ? latestLog : Strings.Check_LogsMissing));
        checks.Add(new ProfileHealthCheck(
            latestDump is null ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
            Strings.Check_CrashDump,
            latestDump is not null
                ? LocalizedText.Format(Strings.Check_CrashDumpFoundFormat, latestDump)
                : Strings.Check_CrashDumpMissing));

        return new ProfileHealthReport(
            checks,
            profileFolderPath,
            savedGamesPath,
            latestLog,
            latestDump,
            LaunchPlan: launchPlan?.Plan,
            OverlayManifest: overlayManifest,
            UsvfsLogPath: File.Exists(usvfsLogPath) ? usvfsLogPath : null);
    }

    private static void AddFsgameSourceCheck(
        List<ProfileHealthCheck> checks,
        ModProfile profile,
        FileLayerPlan? fileLayerPlan)
    {
        try
        {
            var source = fileLayerPlan?.FindFsgameSource() ?? FileLayerPlan.ResolveFsgameSource(profile);
            if (source is null)
            {
                checks.Add(new ProfileHealthCheck(
                    ProfileHealthStatus.Warning,
                    Strings.Check_FsgameSource,
                    Strings.Check_FsgameMissing));
                return;
            }

            ProfileDataConfigurator.ValidateFsgameSource(source.FullPath);
            var selection = !string.IsNullOrWhiteSpace(profile.FsgameSourcePath)
                ? Strings.Check_SelectedManually
                : FileLayerPlan.ResolveFsgameLaunchArgument(profile.LaunchArguments) is null
                    ? Strings.Check_FoundAutomatically
                    : Strings.Check_FoundByFsltx;
            checks.Add(new ProfileHealthCheck(
                ProfileHealthStatus.Healthy,
                Strings.Check_FsgameSource,
                LocalizedText.Format(Strings.Check_SourceDetailsFormat, selection, source.FullPath, Environment.NewLine, source.SourceName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            checks.Add(new ProfileHealthCheck(ProfileHealthStatus.Error, Strings.Check_FsgameSource, ex.Message));
        }
    }

    private static LaunchPlanResolution? TryCreateLaunchPlan(
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        string profileFolderPath,
        CancellationToken cancellationToken)
    {
        if (profile.IsStandalone)
        {
            return ProfileLaunchPlanResolver.PreviewStandalone(profile, cancellationToken);
        }

        if (fileLayerPlan is null || string.IsNullOrWhiteSpace(profileFolderPath))
        {
            return null;
        }

        return profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
            ? ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, fileLayerPlan)
            : ProfileLaunchPlanResolver.PreviewLinkedWorkspace(profile, fileLayerPlan, profileFolderPath);
    }

    private static OverlayManifest? TryCreateOverlayManifest(
        ModProfile profile,
        FileLayerPlan? fileLayerPlan,
        string profileFolderPath,
        CancellationToken cancellationToken)
    {
        if (fileLayerPlan is null || string.IsNullOrWhiteSpace(profileFolderPath))
        {
            return null;
        }

        return profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
            ? OverlayManifestBuilder.BuildVirtualFileSystem(
                profile,
                fileLayerPlan,
                profileFolderPath,
                cancellationToken: cancellationToken)
            : OverlayManifestBuilder.BuildLinkedWorkspace(
                profile,
                fileLayerPlan,
                profileFolderPath,
                cancellationToken: cancellationToken);
    }

    private static void AddProfileStorageChecks(List<ProfileHealthCheck> checks, ModProfile profile)
    {
        var workspacePath = profile.WorkspacePath;
        var usesVirtualFileSystem = profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem;
        var checkTitle = usesVirtualFileSystem ? "USVFS" : "Workspace";
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            checks.Add(new ProfileHealthCheck(
                ProfileHealthStatus.Warning,
                checkTitle,
                usesVirtualFileSystem
                    ? Strings.Check_ProfilePathPendingUsvfs
                    : Strings.Check_PathPending));
            return;
        }

        if (!Directory.Exists(workspacePath))
        {
            checks.Add(new ProfileHealthCheck(
                ProfileHealthStatus.Warning,
                checkTitle,
                LocalizedText.Format(Strings.Check_ProfileFolderCreatedFormat, workspacePath)));
            return;
        }

        var markerExists = File.Exists(Path.Combine(workspacePath, ".stalker-launcher-workspace"));
        checks.Add(new ProfileHealthCheck(
            markerExists ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Error,
            checkTitle,
            markerExists
                ? usesVirtualFileSystem
                    ? LocalizedText.Format(Strings.Check_ProfileDataSeparateFormat, workspacePath)
                    : LocalizedText.Format(Strings.Check_ManagedFolderFormat, workspacePath)
                : LocalizedText.Format(Strings.Check_MarkerMissingFormat, workspacePath)));

        if (usesVirtualFileSystem)
        {
            return;
        }

        var currentExists = Directory.Exists(Path.Combine(workspacePath, "current"));
        var manifestExists = File.Exists(Path.Combine(workspacePath, "build-manifest.json"));
        checks.Add(new ProfileHealthCheck(
            currentExists && manifestExists ? ProfileHealthStatus.Healthy : ProfileHealthStatus.Warning,
            Strings.Check_WorkspaceCache,
            currentExists && manifestExists ? Strings.Check_WorkspacePresent : Strings.Check_WorkspacePending));
    }

    private static FileLayerPlan? TryCreateLinkedFileLayerPlan(ModProfile profile, string profileFolderPath)
    {
        if (profile.IsStandalone ||
            string.IsNullOrWhiteSpace(profile.GameInstallPath) ||
            string.IsNullOrWhiteSpace(profileFolderPath))
        {
            return null;
        }

        return FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, profileFolderPath);
    }

    private static LaunchExecutableResolution? FindExecutableSource(
        ModProfile profile,
        string gamePath,
        FileLayerPlan? fileLayerPlan,
        CancellationToken cancellationToken)
    {
        var roots = fileLayerPlan is null
            ? CreateExecutableRoots(profile, gamePath).ToArray()
            : FileLayerSourceResolver.CreateExecutableRoots(fileLayerPlan);
        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Check_LaunchBinary);
        }
        catch
        {
            return null;
        }

        return ProfileLaunchPlanResolver.ResolveExecutableSource(
            profile,
            roots,
            profile.ExecutableRelativePath,
            allowPinnedSource: true,
            allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(profile.ExecutableRelativePath),
            cancellationToken);
    }

    private static IEnumerable<LaunchExecutableSearchRoot> CreateExecutableRoots(ModProfile profile, string gamePath)
    {
        if (!profile.IsStandalone)
        {
            yield return new LaunchExecutableSearchRoot(gamePath, Strings.Creation_BaseGameSource, 0);
        }

        foreach (var mod in profile.Mods
                     .Where(mod => mod.IsEnabled)
                     .OrderBy(mod => mod.Order))
        {
            yield return new LaunchExecutableSearchRoot(mod.SourcePath, LocalizedText.Format(Strings.Creation_ModSourceFormat, mod.Name), mod.Order);
        }
    }

    private static string FormatExecutableSource(LaunchExecutableResolution source, string requestedRelativePath)
    {
        if (!source.IsAvailable)
        {
            return LocalizedText.Format(Strings.Check_ManualSourceMissingFormat, source.FullPath, Environment.NewLine, source.Reason);
        }

        if (source.IsPinned)
        {
            return LocalizedText.Format(Strings.Check_FinalManualFormat, source.FullPath, Environment.NewLine, source.SourceName);
        }

        if (source.UsedRequestedRelativePath)
        {
            return LocalizedText.Format(Strings.Check_FinalAutomaticFormat, source.FullPath, Environment.NewLine, source.SourceName);
        }

        return LocalizedText.Format(
            Strings.Check_FinalFallbackFormat,
            requestedRelativePath,
            Environment.NewLine,
            source.FullPath,
            source.Reason,
            source.SourceName);
    }

    private static int CountFiles(
        string path,
        CancellationToken cancellationToken,
        params string[] extensions)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Count(file =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return extensions.Contains(
                            Path.GetExtension(file),
                            StringComparer.OrdinalIgnoreCase);
                    })
                : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private static string? FindLatest(
        IEnumerable<string> paths,
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken)
    {
        return FindLatest(paths, _ => true, extensions, cancellationToken);
    }

    private static string? FindLatest(
        IEnumerable<string> paths,
        Func<string, bool> predicate,
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken)
    {
        try
        {
            return paths.Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                .Where(file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return predicate(file) &&
                           extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsvfsLog(string? path) =>
        Path.GetFileName(path)?.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase) == true;

}
