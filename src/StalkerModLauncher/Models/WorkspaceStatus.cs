using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public sealed record WorkspaceStatus(string WorkspacePath, bool RootExists)
{
    public static WorkspaceStatus Missing(string path) => new(path, Directory.Exists(path));

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
