using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class WorkspaceExecutableResolver
{
    public static LaunchExecutableDetection? Resolve(string workspaceRoot, string requestedRelativePath, IProgress<string> progress)
    {
        var requestedIsDedicated = LaunchExecutableDetector.IsDedicatedExecutable(requestedRelativePath);
        var best = LaunchExecutableDetector.DetectBest(
            [new LaunchExecutableSearchRoot(workspaceRoot, "workspace", 0)],
            requestedRelativePath,
            requestedIsDedicated);
        if (best is null || best.Score > 50 && best.CandidateCount != 1)
        {
            return null;
        }

        progress.Report(LocalizedText.Format(Strings.Workspace_ExecutableFallbackFormat, requestedRelativePath, best.RelativePath, best.Reason));
        return best;
    }

    public static LaunchExecutableDetection? ResolveStandalone(string modRoot, string requestedRelativePath, CancellationToken cancellationToken)
    {
        return LaunchExecutableDetector.DetectBest(
            [new LaunchExecutableSearchRoot(modRoot, Strings.Profile_Standalone, 1)],
            requestedRelativePath,
            allowDedicated: false,
            cancellationToken);
    }
}
