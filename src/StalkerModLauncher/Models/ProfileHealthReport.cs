using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public enum ProfileHealthStatus
{
    Healthy,
    Warning,
    Error
}

public sealed record ProfileHealthCheck(
    ProfileHealthStatus Status,
    string Title,
    string Details);

public sealed record ProfileHealthReport(
    IReadOnlyList<ProfileHealthCheck> Checks,
    string ProfileFolderPath,
    string SavedGamesPath,
    string? LatestLogPath,
    string? LatestCrashDumpPath,
    WorkspaceStatus? Workspace = null,
    LaunchPlan? LaunchPlan = null,
    OverlayManifest? OverlayManifest = null,
    string? UsvfsLogPath = null)
{
    public int ErrorCount => Checks.Count(check => check.Status == ProfileHealthStatus.Error);
    public int WarningCount => Checks.Count(check => check.Status == ProfileHealthStatus.Warning);
    public bool IsReady => ErrorCount == 0;

    public string Summary => IsReady
        ? WarningCount == 0 ? Strings.Health_Ready : LocalizedText.Format(Strings.Health_ReadyWarningsFormat, WarningCount)
        : LocalizedText.Format(Strings.Health_AttentionFormat, ErrorCount, WarningCount);

    public string ToText(string profileName)
    {
        var lines = new List<string>
        {
            LocalizedText.Format(Strings.Health_ReportProfileFormat, profileName),
            Summary,
            string.Empty
        };

        lines.AddRange(Checks.Select(check =>
            $"[{StatusText(check.Status)}] {check.Title}: {check.Details}"));
        if (Workspace is { Exists: true } workspace)
        {
            lines.Add(string.Empty);
            lines.Add(LocalizedText.Format(Strings.Health_ReportWorkspaceFormat, workspace.LogicalSizeDisplay, workspace.PhysicalSizeDisplay));
            lines.Add(LocalizedText.Format(Strings.Health_ReportFilesFormat, workspace.FileCount, workspace.HardLinkCount, workspace.SymbolicLinkCount, workspace.LocalFileCount));
        }

        if (File.Exists(UsvfsLogPath))
        {
            lines.Add(string.Empty);
            lines.Add(LocalizedText.Format(Strings.Health_ReportUsvfsFormat, UsvfsLogPath));
            try
            {
                lines.AddRange(File.ReadLines(UsvfsLogPath).TakeLast(30));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add(LocalizedText.Format(Strings.Health_ReportUsvfsFailedFormat, ex.Message));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string StatusText(ProfileHealthStatus status) => status switch
    {
        ProfileHealthStatus.Healthy => Strings.Health_StatusHealthy,
        ProfileHealthStatus.Warning => Strings.Health_StatusWarning,
        _ => Strings.Health_StatusError
    };
}
