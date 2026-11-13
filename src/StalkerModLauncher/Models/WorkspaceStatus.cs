using System.Globalization;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public sealed record WorkspaceStatus(
    string WorkspacePath,
    bool Exists,
    long LogicalSizeBytes,
    long PhysicalSizeBytes,
    int FileCount,
    int SymbolicLinkCount,
    int HardLinkCount,
    int LocalFileCount,
    DateTime? BuiltAtUtc,
    bool StatisticsAvailable = true,
    bool RootExists = false,
    bool CurrentExists = false,
    bool ManifestExists = false)
{
    public static WorkspaceStatus Missing(string path) =>
        new(path, false, 0, 0, 0, 0, 0, 0, null, false, Directory.Exists(path), false, false);

    public string WorkspacePathDisplay => string.IsNullOrWhiteSpace(WorkspacePath)
        ? Strings.Workspace_PathPending
        : WorkspacePath;
    public string CurrentPathDisplay => string.IsNullOrWhiteSpace(WorkspacePath)
        ? Strings.Workspace_CurrentPending
        : Path.Combine(WorkspacePath, "current");
    public string UserDataPathDisplay => string.IsNullOrWhiteSpace(WorkspacePath)
        ? Strings.Workspace_UserdataPending
        : Path.Combine(WorkspacePath, "userdata");
    public string LogicalSizeDisplay => StatisticsAvailable ? FormatSize(LogicalSizeBytes) : Strings.Workspace_AfterRebuild;
    public string PhysicalSizeDisplay => StatisticsAvailable ? FormatSize(PhysicalSizeBytes) : Strings.Workspace_AfterRebuild;
    public string FileCountDisplay => StatisticsAvailable ? $"{FileCount:N0}" : Strings.Workspace_AfterRebuild;
    public string LinkSummaryDisplay => StatisticsAvailable
        ? LocalizedText.Format(Strings.Workspace_LinkSummaryFormat, HardLinkCount, SymbolicLinkCount, LocalFileCount)
        : Strings.Workspace_StatsPending;
    public string BuiltAtDisplay => BuiltAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? Strings.Workspace_NotPrepared;
    public string StateDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkspacePath))
            {
                return Strings.Workspace_PathMissing;
            }

            if (!RootExists)
            {
                return Strings.Workspace_FolderMissing;
            }

            if (!CurrentExists)
            {
                return Strings.Workspace_FilesMissing;
            }

            return ManifestExists
                ? Strings.Workspace_Ready
                : Strings.Workspace_RebuildRecommended;
        }
    }

    public string SizeExplanationDisplay => StatisticsAvailable
        ? Strings.Workspace_SizeTooltip
        : Strings.Workspace_SizePending;

    public static string FormatSize(long bytes)
    {
        string[] units = [Strings.Size_Bytes, Strings.Size_Kilobytes, Strings.Size_Megabytes, Strings.Size_Gigabytes, Strings.Size_Terabytes];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }
}
