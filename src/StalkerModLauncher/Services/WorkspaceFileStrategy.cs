namespace StalkerModLauncher.Services;

public static class WorkspaceFileStrategy
{
    public static bool MustCopy(string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.GetFileName(normalized).Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase) ||
               ProfileWritableGameFiles.Rules.Any(rule =>
                   rule.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MustCopy(string relativePath, string sourceFile, string workspaceFile)
    {
        if (MustCopy(relativePath))
        {
            return true;
        }

        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.StartsWith($"gamedata{Path.DirectorySeparatorChar}scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   Path.GetPathRoot(Path.GetFullPath(sourceFile)),
                   Path.GetPathRoot(Path.GetFullPath(workspaceFile)),
                   StringComparison.OrdinalIgnoreCase);
    }
}
