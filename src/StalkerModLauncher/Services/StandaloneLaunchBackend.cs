using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class StandaloneLaunchBackend : IProfileLaunchBackend
{
    public LaunchBackendKind Kind => LaunchBackendKind.Standalone;

    public Task<LaunchPlan> PrepareAsync(
        ProfileLaunchBackendContext context,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (!context.Profile.IsStandalone)
            throw new InvalidOperationException(Strings.Error_StandaloneBackendRequired);

        cancellationToken.ThrowIfCancellationRequested();
        var resolution = ProfileLaunchPlanResolver.PreviewStandalone(context.Profile, cancellationToken);
        if (!resolution.IsReady || resolution.Plan is null)
            throw new InvalidOperationException(resolution.Error ?? Strings.Error_StandaloneNoModHelp);

        context.Profile.ExecutableRelativePath = resolution.Executable!.RelativePath;
        var relative = Path.GetRelativePath(
            context.Profile.Mods.Single(mod => mod.IsEnabled).SourcePath,
            resolution.Plan.WorkingDirectory);
        context.Profile.WorkingDirectoryRelative = relative == "." ? string.Empty : relative;
        progress.Report(LocalizedText.Format(Strings.Progress_StandaloneReadyFormat, context.Profile.Name));
        return Task.FromResult(resolution.Plan);
    }
}
