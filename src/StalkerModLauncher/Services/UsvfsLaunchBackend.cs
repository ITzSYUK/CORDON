using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class UsvfsLaunchBackend : IProfileLaunchBackend
{
    private readonly IUsvfsRuntime _runtime;
    private readonly IUsvfsRuntime _x86Runtime;
    private readonly string? _runtimeDirectory;
    public UsvfsLaunchBackend(
        IUsvfsRuntime runtime,
        string? runtimeDirectory = null,
        IUsvfsRuntime? x86Runtime = null)
    {
        _runtime = runtime;
        _x86Runtime = x86Runtime ?? runtime;
        _runtimeDirectory = runtimeDirectory;
    }

    public LaunchBackendKind Kind => LaunchBackendKind.VirtualFileSystem;

    public Task<LaunchPlan> PrepareAsync(
        ProfileLaunchBackendContext context,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (!UsvfsFeatureGate.IsEnabled(_runtimeDirectory))
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_UsvfsBackendUnavailableFormat,
                UsvfsFeatureGate.EnableEnvironmentVariable));
        }

        if (context.Profile.IsStandalone)
        {
            throw new InvalidOperationException(Strings.Error_UsvfsLayeredOnly);
        }

        if (context.FileLayerPlan is null || context.OverlayManifest is null)
        {
            throw new InvalidOperationException(Strings.Error_UsvfsPlanRequired);
        }

        var profile = context.Profile;
        var profileWorkspace = Path.GetFullPath(Path.Combine(context.OverlayManifest.WriteOverlayRoot, "..", ".."));
        var profileFsgamePath = UsvfsProfileDataPreparer.Prepare(
            context.FileLayerPlan,
            context.OverlayManifest,
            profileWorkspace,
            progress,
            cancellationToken);
        var launchResolution = ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, context.FileLayerPlan);
        if (!launchResolution.IsReady || launchResolution.Plan is null)
        {
            throw new InvalidOperationException(launchResolution.Error ?? Strings.Error_UsvfsLaunchPlanNotReady);
        }

        var launchTarget = AnomalyUsvfsLaunchTargetResolver.Resolve(profile, context.FileLayerPlan, launchResolution);
        if (launchTarget.BypassedLauncher)
        {
            progress.Report(LocalizedText.Format(
                Strings.Progress_UsvfsManualEngineFormat,
                Path.GetFileName(launchTarget.ExecutablePath)));
        }

        progress.Report(LocalizedText.Format(Strings.Progress_UsvfsExecutableSourceFormat, launchTarget.SourceName));
        var architecture = WindowsExecutableArchitectureDetector.Detect(launchTarget.ExecutablePath);
        if (architecture == WindowsExecutableArchitecture.Unknown)
        {
            throw new BadImageFormatException(
                LocalizedText.Format(Strings.Usvfs_TargetArchitectureMissingFormat, launchTarget.ExecutablePath));
        }

        var runtimeFiles = UsvfsRuntimeFiles.Check(_runtimeDirectory);
        if (!runtimeFiles.IsReadyFor(architecture))
        {
            throw new FileNotFoundException(runtimeFiles.MissingFilesMessage(architecture));
        }

        progress.Report(LocalizedText.Format(Strings.Progress_UsvfsRuntimeVersionFormat, runtimeFiles.RuntimeVersion));
        progress.Report(architecture == WindowsExecutableArchitecture.X86
            ? Strings.Progress_UsvfsArchitectureX86
            : Strings.Progress_UsvfsArchitectureX64);

        var useAnomalyLauncherBootstrap = AnomalyLauncherLocator.IsBaseGameLauncher(
                                             profile.GameInstallPath,
                                             launchTarget.ExecutablePath) ||
                                         AnomalyLauncherLocator.IsLauncherExecutable(
                                             launchTarget.ExecutablePath);
        var isAnomalyEngine = IsAnomalyEngine(launchTarget.ExecutableRelativePath);
        var useSharedAnomalyBootstrap = context.FileLayerPlan.UsesSharedGameData && isAnomalyEngine;
        var usePhysicalAnomalyRoot = isAnomalyEngine && !useSharedAnomalyBootstrap;
        var usePhysicalBaseGameRoot = !useAnomalyLauncherBootstrap &&
                                      !useSharedAnomalyBootstrap &&
                                      ShouldUsePhysicalBaseGameRoot(context.FileLayerPlan, launchTarget);
        var usePhysicalArchiveRoot = !useAnomalyLauncherBootstrap &&
                                     !useSharedAnomalyBootstrap &&
                                     RequiresPhysicalArchiveRoot(context.FileLayerPlan);
        var bootstrapRoot = UsvfsBootstrapPathResolver.Resolve(profileWorkspace);
        UsvfsBootstrapPathResolver.DeleteLegacySharedProfile(
            profileWorkspace,
            context.FileLayerPlan.BaseGame.RootPath,
            profile.Id);
        UsvfsBootstrapResult? bootstrap = null;
        UsvfsExecutableBootstrapper.Clear(profileWorkspace, bootstrapRoot);
        if (!usePhysicalBaseGameRoot)
        {
            bootstrap = useAnomalyLauncherBootstrap
                ? UsvfsExecutableBootstrapper.PrepareAnomalyLauncher(
                    context.FileLayerPlan,
                    launchTarget,
                    bootstrapRoot,
                    context.OverlayManifest.WriteOverlayRoot,
                    progress,
                    cancellationToken)
                : UsvfsExecutableBootstrapper.Prepare(
                    context.FileLayerPlan,
                    launchTarget,
                    bootstrapRoot,
                    progress,
                    cancellationToken);
            MaterializeBootstrapFsgame(
                profileFsgamePath,
                context.OverlayManifest.WriteOverlayRoot,
                bootstrap.RootPath);
        }

        var usePhysicalGameRoot = usePhysicalAnomalyRoot || usePhysicalBaseGameRoot || usePhysicalArchiveRoot;
        var virtualRoot = usePhysicalGameRoot
            ? context.FileLayerPlan.BaseGame.RootPath
            : bootstrap!.RootPath;
        var mappingPlan = useAnomalyLauncherBootstrap || useSharedAnomalyBootstrap
            ? UsvfsMappingPlanBuilder.BuildAnomalyLauncherBootstrap(
                context.FileLayerPlan,
                context.OverlayManifest,
                virtualRoot)
            : UsvfsMappingPlanBuilder.Build(
                context.FileLayerPlan,
                context.OverlayManifest,
                virtualRoot);
        var workingDirectory = usePhysicalGameRoot
            ? launchTarget.WorkingDirectory
            : ResolveBootstrapWorkingDirectory(
                context.FileLayerPlan.BaseGame.RootPath,
                launchTarget.WorkingDirectory,
                bootstrap!.RootPath);
        var executablePath = usePhysicalBaseGameRoot
            ? launchTarget.ExecutablePath
            : bootstrap!.ExecutablePath;
        progress.Report(usePhysicalBaseGameRoot
            ? Strings.Progress_UsvfsRootBase
            : useAnomalyLauncherBootstrap
                ? Strings.Progress_UsvfsRootAnomalyBootstrap
                : usePhysicalAnomalyRoot
                ? Strings.Progress_UsvfsRootAnomaly
                : usePhysicalArchiveRoot
                    ? Strings.Progress_UsvfsRootXray
                : Strings.Progress_UsvfsRootIsolated);
        progress.Report(LocalizedText.Format(Strings.Progress_UsvfsVirtualRootFormat, mappingPlan.VirtualRoot));

        var launchRequest = new UsvfsProcessLaunchRequest(
            executablePath,
            launchTarget.Arguments,
            workingDirectory);
        var selectedRuntime = architecture == WindowsExecutableArchitecture.X86
            ? _x86Runtime
            : _runtime;
        var diagnosticLogPath = UsvfsDiagnosticPaths.Prepare(profileWorkspace, progress);
        progress.Report(LocalizedText.Format(
            Strings.Progress_UsvfsOverlayPlanFormat,
            mappingPlan.Operations.Count,
            diagnosticLogPath));
        var session = selectedRuntime.CreateSession(
            mappingPlan,
            new UsvfsRuntimeOptions(
                $"stalker_launcher_{profile.Id[..Math.Min(profile.Id.Length, 8)]}",
                LogToConsole: false,
                DiagnosticLogPath: diagnosticLogPath),
            progress);

        var processStarter = new Func<LaunchPlan, IProgress<string>?, System.Diagnostics.Process>(
            (_, startProgress) => session.StartProcess(launchRequest, startProgress, cancellationToken));

        return Task.FromResult(new LaunchPlan(
            LaunchBackendKind.VirtualFileSystem,
            launchRequest.ExecutablePath,
            launchRequest.Arguments,
            launchRequest.WorkingDirectory,
            session,
            processStarter,
            () => session.GetExitCodeAsync(),
            session.GetActiveProcessIds));
    }

    private static void MaterializeBootstrapFsgame(
        string? profileFsgamePath,
        string writeOverlayRoot,
        string bootstrapRoot)
    {
        if (string.IsNullOrWhiteSpace(profileFsgamePath) || !File.Exists(profileFsgamePath))
        {
            return;
        }

        string[] sourcePaths = Path.GetFileName(profileFsgamePath).Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase)
            ? [profileFsgamePath]
            : [profileFsgamePath, Path.Combine(Path.GetDirectoryName(profileFsgamePath)!, "fsgame.ltx")];
        foreach (var sourcePath in sourcePaths)
        {
            var relativePath = Path.GetRelativePath(writeOverlayRoot, sourcePath);
            var destination = FileSystemSafety.ResolvePathInside(
                bootstrapRoot,
                relativePath,
                Strings.Safety_BootstrapFsgame);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: true);
        }
    }

    private static bool IsAnomalyEngine(string executableRelativePath)
    {
        var fileName = Path.GetFileName(executableRelativePath);
        return fileName.StartsWith("AnomalyDX", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresPhysicalArchiveRoot(FileLayerPlan layerPlan)
    {
        var fsgame = layerPlan.FindFsgameSource();
        if (fsgame is null)
        {
            return false;
        }

        try
        {
            // X-Ray 1.6 discovers patch/resource archives through these root directories.
            // Keeping the real game root preserves that discovery order while USVFS overlays mods on top.
            return File.ReadLines(fsgame.FullPath, XRayTextEncoding.Config)
                .Any(line => line.TrimStart().StartsWith(
                    "$arch_dir_",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ShouldUsePhysicalBaseGameRoot(
        FileLayerPlan layerPlan,
        UsvfsLaunchTarget launchTarget)
    {
        var executable = layerPlan.FindFinalFile(launchTarget.ExecutableRelativePath);
        if (executable?.Layer.Kind != FileLayerKind.BaseGame)
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(launchTarget.ExecutableRelativePath) ?? string.Empty;
        foreach (var mod in layerPlan.Mods.Where(layer => Directory.Exists(layer.RootPath)))
        {
            var modExecutableDirectory = executableDirectory.Length == 0
                ? mod.RootPath
                : Path.Combine(mod.RootPath, executableDirectory);
            if (!Directory.Exists(modExecutableDirectory))
            {
                continue;
            }

            if (Directory.EnumerateFiles(modExecutableDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(IsEngineLoaderFile))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEngineLoaderFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBootstrapWorkingDirectory(
        string baseGameRoot,
        string sourceWorkingDirectory,
        string bootstrapRoot)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(baseGameRoot),
            Path.GetFullPath(sourceWorkingDirectory));
        if (relative == ".")
        {
            return bootstrapRoot;
        }

        FileSystemSafety.EnsureRelativePath(relative, Strings.Safety_UsvfsWorkingDirectory);
        var workingDirectory = FileSystemSafety.ResolvePathInside(
            bootstrapRoot,
            relative,
            Strings.Safety_UsvfsWorkingDirectory);
        Directory.CreateDirectory(workingDirectory);
        return workingDirectory;
    }
}
