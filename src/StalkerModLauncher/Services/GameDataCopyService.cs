using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class GameDataCopyService
{
    // Service stores belong to the profile, even when game saves are shared.
    private static readonly HashSet<string> ServiceDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "overwrite", "writable-game-files", "usvfs-bootstrap"
    };

    public static (int Copied, int Skipped) CopyMissing(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination) ||
            FileSystemSafety.IsDirectoryInside(source, destination) || FileSystemSafety.IsDirectoryInside(destination, source))
            throw new InvalidOperationException(Strings.Copy_SeparateFolders);
        return CopyMissingCore(source, destination);
    }

    internal static (int Copied, int Skipped) CopyMissingFromProfileOverwrite(string source, string destination)
    {
        var overwrite = Path.Combine(Path.GetFullPath(destination), "overwrite");
        if (!FileSystemSafety.IsDirectoryInside(source, overwrite))
            throw new InvalidOperationException(LocalizedText.Format(Strings.Copy_SourceOutsideOverwriteFormat, overwrite));
        return CopyMissingCore(source, destination);
    }

    private static (int Copied, int Skipped) CopyMissingCore(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Copy_NoDataFormat, source));
        EnsureNoLinks(source);
        EnsureNoLinks(destination);
        var copied = 0;
        var skipped = 0;
        CopyDirectory(source, destination, true);
        return (copied, skipped);

        void CopyDirectory(string from, string to, bool topLevel)
        {
            EnsureNoLinks(to);
            Directory.CreateDirectory(to);
            foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || topLevel && ServiceDirectories.Contains(entry.Name))
                {
                    skipped++;
                    continue;
                }
                var target = Path.Combine(to, entry.Name);
                if (entry is DirectoryInfo)
                {
                    if (File.Exists(target)) { skipped++; continue; }
                    CopyDirectory(entry.FullName, target, false);
                    continue;
                }
                if (Path.Exists(target)) { skipped++; continue; }
                // Publish complete files only; cancellation or an I/O failure never leaves a partial save.
                var temporary = Path.Combine(to, $".copy-{Guid.NewGuid():N}");
                try
                {
                    File.Copy(entry.FullName, temporary, overwrite: false);
                    File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
                    File.Move(temporary, target, overwrite: false);
                    copied++;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(LocalizedText.Format(Strings.Copy_ThroughLinkForbiddenFormat, directory.FullName));
    }
}
