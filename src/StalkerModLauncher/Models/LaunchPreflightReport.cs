using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public sealed record LaunchPreflightReport(
    IReadOnlyList<ProfileHealthCheck> Checks,
    LaunchPlan? LaunchPlan = null,
    OverlayManifest? OverlayManifest = null)
{
    public int ErrorCount => Checks.Count(check => check.Status == ProfileHealthStatus.Error);
    public int WarningCount => Checks.Count(check => check.Status == ProfileHealthStatus.Warning);
    public bool CanLaunch => ErrorCount == 0;

    public string ToErrorMessage()
    {
        var errors = Checks
            .Where(check => check.Status == ProfileHealthStatus.Error)
            .Select(check => $"• {check.Title}: {check.Details}");
        return Strings.Preflight_Failed +
               Environment.NewLine + Environment.NewLine +
               string.Join(Environment.NewLine, errors);
    }
}
