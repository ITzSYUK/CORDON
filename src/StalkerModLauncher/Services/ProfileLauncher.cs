using System.Diagnostics;
using StalkerModLauncher.Models;

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
        if (!_backends.ContainsKey(LaunchBackendKind.LinkedWorkspace))
        {
            throw new ArgumentException("The linked workspace launch backend must be registered.", nameof(backends));
        }

        _launchPlanExecutor = launchPlanExecutor ?? new LaunchPlanExecutor();
        _profileManager = profileManager;
    }

    public async Task<ProfileLaunchHandle> LaunchAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LaunchCoreAsync(gamePath, profile, progress, cancellationToken);
        }
        catch (Exception ex) when (
            profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem &&
            ex is not OperationCanceledException)
        {
            progress.Report($"Ошибка запуска USVFS: {ex.Message}");
            progress.Report(
                "Профиль не переключён на Workspace автоматически. " +
                "Чтобы повторить запуск через Workspace, выберите этот режим в настройках профиля.");
            throw;
        }
    }

    private async Task<ProfileLaunchHandle> LaunchCoreAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var backend = ResolveBackend(profile.LaunchBackendKind);
        progress.Report($"Режим запуска: {(backend.Kind == LaunchBackendKind.VirtualFileSystem ? "USVFS" : "Workspace")}.");
        var context = CreateBackendContext(gamePath, profile, progress);
        var plan = await backend.PrepareAsync(context, progress, cancellationToken);
        progress.Report($"Запуск: {plan.ExecutablePath}");
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

        var fileLayerPlan = FileLayerPlan.CreateLinkedWorkspace(gamePath, profile, workspace);
        var overlayManifest = profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem
            ? OverlayManifestBuilder.BuildVirtualFileSystem(profile, fileLayerPlan, workspace)
            : OverlayManifestBuilder.BuildLinkedWorkspace(profile, fileLayerPlan, workspace);
        return new ProfileLaunchBackendContext(gamePath, profile, fileLayerPlan, overlayManifest);
    }

    private IProfileLaunchBackend ResolveBackend(LaunchBackendKind kind)
    {
        if (_backends.TryGetValue(kind, out var backend))
        {
            return backend;
        }

        throw new InvalidOperationException(kind == LaunchBackendKind.VirtualFileSystem
            ? "Для профиля выбран USVFS, но его компоненты недоступны. Установите сборку лаунчера с файлами usvfs_x64.dll и usvfs_proxy_x64.exe либо выберите Workspace в настройках профиля."
            : $"Система запуска профиля недоступна: {kind}.");
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
