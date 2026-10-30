using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public interface IProfileLauncher
{
    Task<ProfileLaunchHandle> LaunchAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileLauncher : IProfileLauncher
{
    private readonly Dictionary<LaunchBackendKind, IProfileLaunchBackend> _backends;
    private readonly ILaunchPlanExecutor _launchPlanExecutor;
    private readonly ProfileManager? _profileManager;

    public ProfileLauncher(
        IEnumerable<IProfileLaunchBackend> backends,
        ILaunchPlanExecutor? launchPlanExecutor = null,
        ProfileManager? profileManager = null)
    {
        _backends = backends.ToDictionary(backend => backend.Kind);

        _launchPlanExecutor = launchPlanExecutor ?? new LaunchPlanExecutor();
        _profileManager = profileManager;
    }

    public async Task<ProfileLaunchHandle> LaunchAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (profile.IsRunning || profile.IsPreparing)
            throw new InvalidOperationException(Strings.ProfileStorage_Busy);
        profile.IsPreparing = true;
        try
        {
            return await LaunchCoreAsync(gamePath, profile, progress, cancellationToken);
        }
        catch (Exception ex) when (
            !profile.IsStandalone &&
            ex is not OperationCanceledException)
        {
            progress.Report(LocalizedText.Format(Strings.Launch_UsvfsFailedFormat, ex.Message));
            progress.Report(Strings.Launch_UsvfsNoAutoFallback);
            throw;
        }
        finally { profile.IsPreparing = false; }
    }

    private async Task<ProfileLaunchHandle> LaunchCoreAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!profile.IsStandalone && profile.LaunchBackendKind != LaunchBackendKind.VirtualFileSystem)
            throw new InvalidOperationException(Strings.Launch_WorkspaceRemoved);
        var backend = ResolveBackend(profile.IsStandalone ? LaunchBackendKind.Standalone : profile.LaunchBackendKind);
        progress.Report(LocalizedText.Format(Strings.Launch_BackendFormat, backend.Kind == LaunchBackendKind.VirtualFileSystem ? "USVFS" : Strings.Profile_Standalone));
        var context = CreateBackendContext(gamePath, profile, progress);
        var plan = await backend.PrepareAsync(context, progress, cancellationToken);
        progress.Report(LocalizedText.Format(Strings.Launch_ExecutableFormat, plan.ExecutablePath));
        try
        {
            var process = _launchPlanExecutor.Start(plan, progress);
            Task<int>? completion = null;
            if (plan.RuntimeCompletion is not null)
            {
                completion = plan.RuntimeCompletion();
            }
            else if (plan.RuntimeLease is not null)
            {
                completion = WaitForProcessExitAsync(process);
            }

            if (plan.RuntimeLease is not null && completion is not null)
            {
                completion = DisposeRuntimeAfterCompletionAsync(completion, plan.RuntimeLease);
            }

            return new ProfileLaunchHandle(process, completion, plan.ActiveProcessIds);
        }
        catch
        {
            if (plan.RuntimeLease is not null)
            {
                await plan.RuntimeLease.DisposeAsync();
            }

            throw;
        }
    }

    private ProfileLaunchBackendContext CreateBackendContext(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress)
    {
        if (profile.IsStandalone || _profileManager is null || string.IsNullOrWhiteSpace(gamePath))
        {
            return new ProfileLaunchBackendContext(gamePath, profile);
        }

        var workspace = _profileManager.EnsureProfileFolderPath(profile, progress);

        var fileLayerPlan = FileLayerPlan.CreateForProfile(gamePath, profile, workspace);
        var overlayManifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, fileLayerPlan, workspace);
        return new ProfileLaunchBackendContext(gamePath, profile, fileLayerPlan, overlayManifest);
    }

    private IProfileLaunchBackend ResolveBackend(LaunchBackendKind kind)
    {
        if (_backends.TryGetValue(kind, out var backend))
        {
            return backend;
        }

        throw new InvalidOperationException(kind == LaunchBackendKind.VirtualFileSystem
            ? Strings.Launch_UsvfsUnavailable
            : Strings.Launch_WorkspaceRemoved);
    }

    private static async Task<int> WaitForProcessExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<int> DisposeRuntimeAfterCompletionAsync(
        Task<int> completion,
        IAsyncDisposable runtimeLease)
    {
        try
        {
            return await completion;
        }
        finally
        {
            try
            {
                await runtimeLease.DisposeAsync();
            }
            catch
            {
                // Runtime cleanup must not crash the launcher after the game exits.
            }
        }
    }
}
