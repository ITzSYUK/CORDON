namespace StalkerModLauncher.Services;

public static class FileSystemSafety
{
    public static void EnsureRelativePath(string relativePath, string displayName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException($"{displayName} is empty.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"{displayName} must be a relative path inside the profile workspace.");
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"{displayName} must not leave the profile workspace.");
        }

        var invalidChars = Path.GetInvalidPathChars();
        if (relativePath.Any(invalidChars.Contains))
        {
            throw new InvalidOperationException($"{displayName} contains invalid path characters.");
        }
    }

    public static string ResolvePathInside(string rootPath, string relativePath, string displayName)
    {
        EnsureRelativePath(relativePath, displayName);

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!IsDirectoryInside(fullPath, fullRoot))
        {
            throw new InvalidOperationException($"{displayName} must stay inside: {fullRoot}");
        }

        return fullPath;
    }

    public static void EnsureDirectoryInside(string childPath, string rootPath)
    {
        if (!IsDirectoryInside(childPath, rootPath))
        {
            throw new InvalidOperationException($"Refusing to operate outside the managed workspace: {Path.GetFullPath(childPath)}");
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
