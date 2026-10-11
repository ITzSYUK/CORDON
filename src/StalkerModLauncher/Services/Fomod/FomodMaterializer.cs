namespace StalkerModLauncher.Services.Fomod;

/// <summary>
/// Copies the resolved FOMOD file list from the package root into the staging
/// directory, replacing the full extraction (unselected options are dropped).
/// The fomod script folder itself is never installed.
/// </summary>
public static class FomodMaterializer
{
    /// <returns>Number of materialized files.</returns>
    public static int ApplyToStaging(
        string packageRoot,
        string stagingPath,
        string installRoot,
        IReadOnlyList<FomodFileEntry> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(files);

        var fullPackageRoot = Path.GetFullPath(packageRoot);
        var fullStaging = Path.GetFullPath(stagingPath);
        var fullInstallRoot = Path.GetFullPath(installRoot);
        FileSystemSafety.EnsureDirectoryInside(fullPackageRoot, fullStaging);
        FileSystemSafety.EnsureDirectoryInside(fullStaging, fullInstallRoot);
        if (FileSystemSafety.IsSameDirectory(fullStaging, fullInstallRoot))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, stagingPath));
        }

        var outputPath = fullStaging.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-fomod-" + Guid.NewGuid().ToString("N");
        FileSystemSafety.EnsureDirectoryInside(outputPath, fullInstallRoot);
        try
        {
            var count = CopyToStaging(fullPackageRoot, outputPath, fullInstallRoot, files, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(fullStaging, recursive: true);
            Directory.Move(outputPath, fullStaging);
            return count;
        }
        catch
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }

            throw;
        }
    }

    /// <summary>Copies selected files to a new managed directory without modifying the source.</summary>
    public static int CopyToStaging(
        string packageRoot,
        string stagingPath,
        string installRoot,
        IReadOnlyList<FomodFileEntry> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(files);
        var source = Path.GetFullPath(packageRoot);
        var target = Path.GetFullPath(stagingPath);
        FileSystemSafety.EnsureDirectoryInside(target, installRoot);
        if (FileSystemSafety.IsSameDirectory(target, installRoot) ||
            FileSystemSafety.IsDirectoryInside(target, source) || FileSystemSafety.IsDirectoryInside(source, target))
        {
            throw new InvalidDataException(Resources.Strings.Fomod_DestinationInsideSource);
        }
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException(Resources.LocalizedText.Format(Resources.Strings.Archive_InstallFolderUnavailableFormat, target));
        }

        Directory.CreateDirectory(target);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyEntry(source, target, file, cancellationToken);
            }
            var count = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count();
            if (count == 0) throw new InvalidDataException(Resources.Strings.Archive_Empty);
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
        catch
        {
            FileSystemSafety.DeleteDirectoryContents(target, installRoot);
            throw;
        }
    }

    private static void CopyEntry(string packageRoot, string outputPath, FomodFileEntry file, CancellationToken cancellationToken)
    {
        var sourceRelative = NormalizeRelative(file.Source);
        var sourcePath = Path.GetFullPath(Path.Combine(packageRoot, sourceRelative));
        if (!FileSystemSafety.IsDirectoryInside(sourcePath, packageRoot))
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, file.Source));
        }

        var destinationRelative = NormalizeRelative(file.Destination);
        var destinationBase = string.IsNullOrEmpty(destinationRelative)
            ? outputPath
            : Path.GetFullPath(Path.Combine(outputPath, destinationRelative));
        if (!FileSystemSafety.IsDirectoryInside(destinationBase, outputPath))
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, file.Destination));
        }

        if (File.Exists(sourcePath))
        {
            EnsureNoLinks(packageRoot, sourcePath);
            var target = string.IsNullOrEmpty(destinationRelative) || file.Destination.EndsWith('/') || file.Destination.EndsWith('\\')
                ? Path.Combine(destinationBase, Path.GetFileName(sourcePath))
                : destinationBase;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourcePath, target, overwrite: true);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        else if (Directory.Exists(sourcePath))
        {
            CopyDirectory(packageRoot, sourcePath, destinationBase, cancellationToken);
        }
        else
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodMissingSourceFormat, file.Source));
        }
    }

    private static void CopyDirectory(string packageRoot, string source, string destination, CancellationToken cancellationToken)
    {
        EnsureNoLinks(packageRoot, source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(packageRoot, file);
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileSystemSafety.IsSameDirectory(source, packageRoot) && Path.GetFileName(directory).Equals("fomod", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            CopyDirectory(packageRoot, directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }

    internal static string NormalizeRelative(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized) || normalized.Contains(':') || normalized.Contains('\0'))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, value));
        }
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".." || segment.EndsWith(' ') || segment.EndsWith('.') && segment != "."))
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, value));
        }

        return Path.Combine(segments.Where(segment => segment != ".").ToArray());
    }

    internal static void EnsureNoLinks(string root, string path)
    {
        for (var current = path; current is not null && FileSystemSafety.IsDirectoryInside(current, root); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, path));
            }
            if (FileSystemSafety.IsSameDirectory(current, root))
            {
                break;
            }
        }
    }
}
