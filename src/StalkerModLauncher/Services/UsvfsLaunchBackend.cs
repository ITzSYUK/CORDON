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
        if (context.Profile.IsStandalone)
        {
            throw new InvalidOperationException(Strings.Error_UsvfsLayeredOnly);
        }

        if (context.FileLayerPlan is null || context.OverlayManifest is null)
        {
            throw new InvalidOperationException(Strings.Error_UsvfsPlanRequired);
        }

        var profile = context.Profile;
        // Capture only launch inputs; the worker must not read WPF's live profile or mod collection.
        var snapshot = new ModProfile
        {
            Id = profile.Id,
            GameInstallPath = profile.GameInstallPath,
            ExecutableRelativePath = profile.ExecutableRelativePath,
            ExecutableSourcePath = profile.ExecutableSourcePath,
            LaunchArguments = profile.LaunchArguments,
            UsvfsExecutableOverrideRelativePath = profile.UsvfsExecutableOverrideRelativePath,
            Mods = new(profile.Mods.Select(mod => new ModEntry
            {
                Name = mod.Name,
                SourcePath = mod.SourcePath,
                Order = mod.Order,
                IsEnabled = mod.IsEnabled
            }))
        };
        return Task.Run(() => PrepareCoreAsync(context with { Profile = snapshot }, progress, cancellationToken), cancellationToken);
    }

    private async Task<LaunchPlan> PrepareCoreAsync(
        ProfileLaunchBackendContext context,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!UsvfsFeatureGate.IsEnabled(_runtimeDirectory))
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_UsvfsBackendUnavailableFormat,
                UsvfsFeatureGate.EnableEnvironmentVariable));
        }

        var profile = context.Profile;
        var layerPlan = context.FileLayerPlan!;
        var manifest = context.OverlayManifest!;
        var profileWorkspace = Path.GetFullPath(Path.Combine(manifest.WriteOverlayRoot, "..", ".."));
        var bootstrapRoot = UsvfsBootstrapPathResolver.Resolve(profileWorkspace);
        var profileFsgamePath = UsvfsProfileDataPreparer.Prepare(
            layerPlan,
            manifest,
            profileWorkspace,
            progress,
            cancellationToken);
        var launchResolution = ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, layerPlan);
        if (!launchResolution.IsReady || launchResolution.Plan is null)
        {
            throw new InvalidOperationException(launchResolution.Error ?? Strings.Error_UsvfsLaunchPlanNotReady);
        }

        var launchTarget = AnomalyUsvfsLaunchTargetResolver.Resolve(profile, layerPlan, launchResolution, forSavedGame: context.SavedGamePath is not null);
        if (context.SavedGamePath is { } savedGamePath)
        {
            var catalog = SavedGameCatalog.ForVirtualTarget(layerPlan, launchTarget, profileFsgamePath);
            var saveName = SavedGameLaunch.GetSaveName(catalog, savedGamePath);
            launchTarget = launchTarget with
            {
                Arguments = SavedGameLaunch.AppendArguments(launchTarget.Arguments, saveName, launchTarget.EngineCommandLinePath)
            };
        }
        if (launchTarget.BypassedLauncher)
        {
            progress.Report(LocalizedText.Format(
                context.SavedGamePath is not null && string.IsNullOrWhiteSpace(profile.UsvfsExecutableOverrideRelativePath)
                    ? Strings.Saves_AnomalyLaunchFormat
                    : Strings.Progress_UsvfsManualEngineFormat,
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

        var useAnomalyLauncherBootstrap = IsAnomalyLauncher(profile.GameInstallPath, launchTarget.ExecutablePath);
        var isAnomalyEngine = IsAnomalyEngine(launchTarget.ExecutableRelativePath);
        var hasPhysicalWorkingDirectory = Directory.Exists(launchTarget.WorkingDirectory);
        var useVirtualExecutable = CanUseVirtualExecutablePath(
            layerPlan, launchTarget, useAnomalyLauncherBootstrap, isAnomalyEngine);
        var useSharedAnomalyBootstrap = layerPlan.UsesSharedGameData && isAnomalyEngine;
        var usePhysicalAnomalyRoot = isAnomalyEngine && !useSharedAnomalyBootstrap && hasPhysicalWorkingDirectory;
        var usePhysicalBaseGameRoot = !useAnomalyLauncherBootstrap &&
                                      !useSharedAnomalyBootstrap &&
                                      hasPhysicalWorkingDirectory &&
                                      ShouldUsePhysicalBaseGameRoot(layerPlan, launchTarget);
        var usePhysicalArchiveRoot = !useAnomalyLauncherBootstrap &&
                                     !useSharedAnomalyBootstrap &&
                                     hasPhysicalWorkingDirectory &&
                                     RequiresPhysicalArchiveRoot(layerPlan);
        UsvfsBootstrapPathResolver.DeleteLegacySharedProfile(
            profileWorkspace,
            layerPlan.BaseGame.RootPath,
            profile.Id,
            preserveFiles: true,
            cancellationToken: cancellationToken);
        string? bootstrapExecutable = null;
        UsvfsBootstrapFileStore.PreserveBeforeClear(bootstrapRoot, profileWorkspace, cancellationToken);
        UsvfsBootstrapFileStore.PreserveBeforeClear(Path.Combine(profileWorkspace, "userdata", "usvfs-bootstrap"), profileWorkspace, cancellationToken);
        UsvfsExecutableBootstrapper.Clear(profileWorkspace, bootstrapRoot);
        if (!usePhysicalBaseGameRoot && !useVirtualExecutable)
        {
            bootstrapExecutable = useAnomalyLauncherBootstrap
                ? UsvfsExecutableBootstrapper.PrepareAnomalyLauncher(
                    layerPlan,
                    launchTarget,
                    bootstrapRoot,
                    progress,
                    cancellationToken)
                : UsvfsExecutableBootstrapper.Prepare(
                    layerPlan,
                    launchTarget,
                    bootstrapRoot,
                    progress,
                    cancellationToken);
            MaterializeBootstrapFsgame(profileFsgamePath, manifest.WriteOverlayRoot, bootstrapRoot);
        }

        var usePhysicalGameRoot = usePhysicalAnomalyRoot || usePhysicalBaseGameRoot || usePhysicalArchiveRoot || useVirtualExecutable;
        var virtualRoot = usePhysicalGameRoot
            ? layerPlan.BaseGame.RootPath
            : bootstrapRoot;
        var mappingPlan = useAnomalyLauncherBootstrap || useSharedAnomalyBootstrap
            ? UsvfsMappingPlanBuilder.BuildAnomalyLauncherBootstrap(
                layerPlan,
                manifest,
                virtualRoot)
            : UsvfsMappingPlanBuilder.Build(
                layerPlan,
                manifest,
                virtualRoot);
        var workingDirectory = usePhysicalGameRoot
            ? launchTarget.WorkingDirectory
            : ResolveBootstrapWorkingDirectory(
                layerPlan.BaseGame.RootPath,
                launchTarget.WorkingDirectory,
                bootstrapRoot);
        var executablePath = useVirtualExecutable
            ? Path.GetFullPath(Path.Combine(virtualRoot, launchTarget.ExecutableRelativePath))
            : bootstrapExecutable ?? launchTarget.ExecutablePath;
        if (useVirtualExecutable)
        {
            var imageRoot = hasPhysicalWorkingDirectory ? bootstrapRoot : Path.Combine(bootstrapRoot, "images");
            var originalOperationCount = mappingPlan.Operations.Count;
            mappingPlan = UsvfsExecutableBootstrapper.PrepareVirtualExecutables(
                mappingPlan, launchTarget, imageRoot, progress, cancellationToken);
            IEnumerable<UsvfsMappingOperation> primaryOperations = mappingPlan.Operations;
            IReadOnlyList<UsvfsMappingOperation> workingOperations = [];
            if (!hasPhysicalWorkingDirectory)
            {
                // Win32 needs a physical CWD. Map the game tree around it so relative paths still see every layer.
                var workingRoot = Path.Combine(bootstrapRoot, "working-tree");
                workingDirectory = ResolveBootstrapWorkingDirectory(virtualRoot, workingDirectory, workingRoot);
                var workingTree = UsvfsMappingPlanBuilder.Build(layerPlan, manifest, workingRoot);
                var workingFiles = mappingPlan.Operations.Skip(originalOperationCount).Select(operation => operation with
                {
                    DestinationPath = Path.Combine(workingRoot, Path.GetRelativePath(virtualRoot, operation.DestinationPath))
                });
                workingOperations = UsvfsMappingPlanBuilder.EnsureMappedParents(
                    workingTree.Operations.Concat(workingFiles), manifest, workingRoot);
                primaryOperations = UsvfsExecutableBootstrapper.GetPhysicalImageMappings(layerPlan.BaseGame, cancellationToken)
                    .Concat(primaryOperations);
            }
            // Register the game-root mappings last at each priority to preserve the reverse image paths.
            mappingPlan = mappingPlan with
            {
                Operations = workingOperations.Concat(
                    UsvfsMappingPlanBuilder.EnsureMappedParents(primaryOperations, manifest, virtualRoot)).ToArray()
            };
        }

        progress.Report(useVirtualExecutable
            ? Strings.Progress_UsvfsRootVirtualExecutable
            : usePhysicalBaseGameRoot
            ? Strings.Progress_UsvfsRootBase
            : useAnomalyLauncherBootstrap
                ? Strings.Progress_UsvfsRootAnomalyBootstrap
                : usePhysicalAnomalyRoot
                ? Strings.Progress_UsvfsRootAnomaly
                : usePhysicalArchiveRoot
                    ? Strings.Progress_UsvfsRootXray
                : Strings.Progress_UsvfsRootIsolated);
        progress.Report(LocalizedText.Format(Strings.Progress_UsvfsVirtualRootFormat, mappingPlan.VirtualRoot));

        var launchRequest = new UsvfsProcessLaunchRequest(executablePath, launchTarget.Arguments, workingDirectory)
        {
            StartFromVirtualPath = useVirtualExecutable
        };
        // Start the x86 helper from the same architecture. USVFS injects the target from there.
        var selectedRuntime = useVirtualExecutable || architecture == WindowsExecutableArchitecture.X86
            ? _x86Runtime
            : _runtime;
        var diagnosticLogPath = UsvfsDiagnosticPaths.Prepare(profileWorkspace, progress);
        UsvfsBootstrapFileStore.Record(bootstrapRoot, useVirtualExecutable && !hasPhysicalWorkingDirectory);
        progress.Report(LocalizedText.Format(
            Strings.Progress_UsvfsOverlayPlanFormat,
            mappingPlan.Operations.Count,
            diagnosticLogPath));
        cancellationToken.ThrowIfCancellationRequested();
        var session = selectedRuntime.CreateSession(
            mappingPlan,
            new UsvfsRuntimeOptions(
                $"stalker_launcher_{profile.Id[..Math.Min(profile.Id.Length, 8)]}",
                LogToConsole: false,
                DiagnosticLogPath: diagnosticLogPath),
            progress);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processStarter = new Func<LaunchPlan, IProgress<string>?, System.Diagnostics.Process>(
                (_, startProgress) => session.StartProcess(launchRequest, startProgress, cancellationToken));

            return new LaunchPlan(
                LaunchBackendKind.VirtualFileSystem,
                executablePath,
                launchTarget.Arguments,
                workingDirectory,
                session,
                processStarter,
                async () =>
                {
                    var exitCode = await session.GetExitCodeAsync().ConfigureAwait(false);
                    if (bootstrapExecutable is not null)
                        ProfileWritableGameFileStore.CaptureFromWorkspace(bootstrapRoot, profileWorkspace, progress);
                    UsvfsBootstrapFileStore.Capture(bootstrapRoot, profileWorkspace);
                    return exitCode;
                },
                session.GetActiveProcessIds);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
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

    internal static bool RequiresVirtualExecutableHost(ModProfile profile, FileLayerPlan layers, UsvfsLaunchTarget target) =>
        CanUseVirtualExecutablePath(layers, target,
            IsAnomalyLauncher(profile.GameInstallPath, target.ExecutablePath),
            IsAnomalyEngine(target.ExecutableRelativePath));

    private static bool CanUseVirtualExecutablePath(
        FileLayerPlan layers, UsvfsLaunchTarget target, bool anomalyLauncher, bool anomalyEngine)
    {
        if (anomalyLauncher || anomalyEngine)
            return false;

        var selectedImage = Path.GetFullPath(target.ExecutablePath);
        return layers.Mods.Any(layer => Path.GetFullPath(Path.Combine(layer.RootPath, target.ExecutableRelativePath))
            .Equals(selectedImage, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnomalyLauncher(string gamePath, string executablePath) =>
        AnomalyLauncherLocator.IsBaseGameLauncher(gamePath, executablePath) ||
        AnomalyLauncherLocator.IsLauncherExecutable(executablePath);

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
