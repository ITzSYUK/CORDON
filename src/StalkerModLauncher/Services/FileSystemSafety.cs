using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class FileSystemSafety
{
    public static void EnsureRelativePath(string relativePath, string displayName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Safety_PathEmptyFormat, displayName));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Safety_PathRelativeFormat, displayName));
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Safety_PathEscapesFormat, displayName));
        }

        var invalidChars = Path.GetInvalidPathChars();
        if (relativePath.Any(invalidChars.Contains))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Safety_PathInvalidCharsFormat, displayName));
        }
    }

    public static string ResolvePathInside(string rootPath, string relativePath, string displayName)
    {
        EnsureRelativePath(relativePath, displayName);

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!IsDirectoryInside(fullPath, fullRoot))
        {
            throw new InvalidOperationException(LocalizedText.Format(Strings.Safety_PathInsideFormat, displayName, fullRoot));
        }

        return fullPath;
    }

    public static void EnsureDirectoryInside(string childPath, string rootPath)
    {
        if (!IsDirectoryInside(childPath, rootPath))
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_OutsideManagedWorkspaceFormat,
                Path.GetFullPath(childPath)));
        }
    }

    public static bool IsDirectoryInside(string childPath, string rootPath)
    {
        var fullChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullChild.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullChild.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameDirectory(string leftPath, string rightPath)
    {
        var fullLeft = Path.GetFullPath(leftPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRight = Path.GetFullPath(rightPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullLeft.Equals(fullRight, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFileSystemRoot(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(Path.GetFullPath(directoryPath))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(root) && fullPath.Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteDirectoryContents(string directoryPath, string allowedRoot)
    {
        EnsureDirectoryInside(directoryPath, allowedRoot);

        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        // Never change attributes here: workspace files can be hard links to original game files,
        // and NTFS attributes are shared by every hard link to the same file.
        Directory.Delete(directoryPath, recursive: true);
    }

    public static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Profile" : sanitized;
    }
}
