using System.Runtime.InteropServices;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class WorkspaceMaterializer
{
    public static void DeleteWorkspaceContents(
        string workspaceDirectory,
        string allowedRoot,
        IProgress<string>? progress = null)
    {
        FileSystemSafety.EnsureNoDirectoryLinks(workspaceDirectory);
        try
        {
            FileSystemSafety.DeleteDirectoryContents(workspaceDirectory, allowedRoot);
            return;
        }
        catch (Exception ex) when (Directory.Exists(workspaceDirectory) &&
                                   (ex is UnauthorizedAccessException or IOException))
        {
            progress?.Report(Strings.Materializer_OldLinksFound);
        }

        var releasedFiles = DeleteReadOnlyWorkspaceFiles(workspaceDirectory);
        progress?.Report(LocalizedText.Format(Strings.Materializer_OldLinksReleasedFormat, releasedFiles));
        FileSystemSafety.DeleteDirectoryContents(workspaceDirectory, allowedRoot);
    }

    public static void ValidateLinkSupport(WorkspaceSourceSnapshot snapshot, string workspaceRoot, IProgress<string> progress)
    {
        var workspaceVolume = Path.GetPathRoot(Path.GetFullPath(workspaceRoot));
        var crossVolumeFiles = new[] { snapshot.Game }.Concat(snapshot.Mods.Values)
            .Select(source => source.Files.FirstOrDefault(file => !WorkspaceFileStrategy.MustCopy(file.RelativePath)))
            .Where(file => file is not null)
            .Cast<SourceFileSnapshot>()
            .Where(file => !string.Equals(Path.GetPathRoot(Path.GetFullPath(file.FullPath)), workspaceVolume, StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => Path.GetPathRoot(Path.GetFullPath(file.FullPath)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (crossVolumeFiles.Length == 0)
        {
            return;
        }

        progress.Report(Strings.Materializer_CheckingSymlinks);
        foreach (var sourceFile in crossVolumeFiles)
        {
            var testLink = Path.Combine(workspaceRoot, $".stalker-launcher-link-test-{Guid.NewGuid():N}");
            try
            {
                if (!TryCreateSymbolicFileLink(testLink, sourceFile.FullPath) || !File.Exists(testLink))
                {
                    throw CreateLinkFailureException(sourceFile.FullPath, testLink);
                }
            }
            finally
            {
                File.Delete(testLink);
            }
        }
    }

    public static void MirrorBaseGame(
        DirectorySnapshot source,
        string targetRoot,
        IProgress<string> progress,
        WorkspaceBuildStats stats,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in source.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetRoot, relativePath));
        }

        var fileCount = 0;
        foreach (var file in source.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = Path.Combine(targetRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            LinkFile(file.FullPath, targetFile, file.RelativePath, stats, IsAnomalyLauncherConfiguration(file.RelativePath));

            if (++fileCount % 500 == 0)
            {
                progress.Report(LocalizedText.Format(Strings.Materializer_GameFilesFormat, fileCount));
            }
        }
    }

    public static void ApplyMod(
        string workspaceRoot,
        FileLayer layer,
        DirectorySnapshot source,
        IProgress<string> progress,
        WorkspaceBuildStats stats,
        CancellationToken cancellationToken)
    {
        progress.Report(LocalizedText.Format(Strings.Materializer_ConnectingModFormat, FileLayerPlan.GetDisplayName(layer)));
        foreach (var relativePath in source.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(workspaceRoot, relativePath));
        }

        foreach (var file in source.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileLayerPlan.IsExcluded(layer, file.RelativePath))
            {
                continue;
            }

            FileSystemSafety.EnsureRelativePath(file.RelativePath, Strings.Safety_ModFile);
            var targetFile = Path.Combine(workspaceRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }

            LinkFile(file.FullPath, targetFile, file.RelativePath, stats, IsAnomalyLauncherConfiguration(file.RelativePath));
        }

        progress.Report(LocalizedText.Format(Strings.Materializer_ModConnectedFormat, FileLayerPlan.GetDisplayName(layer), source.Files.Count, source.Directories.Count));
        if (source.Files.Count == 0)
        {
            progress.Report(LocalizedText.Format(Strings.Materializer_EmptyModFormat, FileLayerPlan.GetDisplayName(layer)));
        }
    }

    public static void ReplaceFile(
        string sourceFile,
        string workspaceRoot,
        string relativePath,
        WorkspaceBuildStats stats)
    {
        FileSystemSafety.EnsureRelativePath(relativePath, Strings.Safety_WorkspaceFile);
        var targetFile = Path.Combine(workspaceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
        }

        LinkFile(sourceFile, targetFile, relativePath, stats);
    }

    private static void LinkFile(
        string sourceFile,
        string targetFile,
        string relativePath,
        WorkspaceBuildStats stats,
        bool forceIndependentCopy = false)
    {
        var length = new FileInfo(sourceFile).Length;
        if (forceIndependentCopy || WorkspaceFileStrategy.MustCopy(relativePath, sourceFile, targetFile))
        {
            CopyIndependentFile(sourceFile, targetFile, relativePath, stats, length, isRequiredLocalFile: true);
            return;
        }

        // A hard link shares the ReadOnly attribute with the source file. The next
        // workspace rebuild would then be unable to remove the link without also
        // changing the user's mod. Prefer a symlink; use a small local copy only
        // when Windows does not permit symbolic links on this machine.
        if ((File.GetAttributes(sourceFile) & FileAttributes.ReadOnly) != 0)
        {
            if (TryCreateSymbolicFileLink(targetFile, sourceFile) && File.Exists(targetFile))
            {
                stats.RecordReadOnly(relativePath, WorkspaceFileKind.SymbolicLink, length);
                return;
            }

            CopyIndependentFile(sourceFile, targetFile, relativePath, stats, length, isReadOnlySource: true);
            return;
        }

        if (TryCreateHardLink(targetFile, sourceFile))
        {
            stats.Record(relativePath, WorkspaceFileKind.HardLink, length);
            return;
        }

        if (TryCreateSymbolicFileLink(targetFile, sourceFile) && File.Exists(targetFile))
        {
            stats.Record(relativePath, WorkspaceFileKind.SymbolicLink, length);
            return;
        }

        File.Delete(targetFile);
        throw CreateLinkFailureException(sourceFile, targetFile);
    }

    private static void CopyIndependentFile(
        string sourceFile,
        string targetFile,
        string relativePath,
        WorkspaceBuildStats stats,
        long length,
        bool isReadOnlySource = false,
        bool isRequiredLocalFile = false)
    {
        File.Copy(sourceFile, targetFile, overwrite: false);
        var attributes = File.GetAttributes(targetFile);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(targetFile, attributes & ~FileAttributes.ReadOnly);
        }

        if (isRequiredLocalFile)
        {
            stats.RecordRequiredLocal(relativePath, length);
        }
        else if (isReadOnlySource)
        {
            stats.RecordReadOnly(relativePath, WorkspaceFileKind.LocalCopy, length);
        }
        else
        {
            stats.Record(relativePath, WorkspaceFileKind.LocalCopy, length);
        }
    }

    private static bool IsAnomalyLauncherConfiguration(string relativePath) =>
        Path.GetFileName(relativePath).Equals("AnomalyLauncher.cfg", StringComparison.OrdinalIgnoreCase);

    private static int DeleteReadOnlyWorkspaceFiles(string workspaceDirectory)
    {
        const uint deleteAccess = 0x00010000;
        const uint shareReadWriteDelete = 7;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const int fileDispositionInfoEx = 21;
        const uint deletePosixIgnoreReadOnly = 0x1 | 0x2 | 0x10;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var releasedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(workspaceDirectory, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                // Delete the directory entry without changing attributes shared with other hard links.
                using var handle = CreateFile(file, deleteAccess, shareReadWriteDelete, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var flags = deletePosixIgnoreReadOnly;
                if (!SetFileInformationByHandle(handle, fileDispositionInfoEx, ref flags, sizeof(uint)))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                releasedFiles++;
            }
        }

        return releasedFiles;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, ref uint flags, uint size);

    private static IOException CreateLinkFailureException(string sourceFile, string targetFile)
    {
        var sourceVolume = GetVolumeDisplayName(sourceFile);
        var workspaceVolume = GetVolumeDisplayName(targetFile);
        var reason = !string.Equals(sourceVolume, workspaceVolume, StringComparison.OrdinalIgnoreCase)
            ? LocalizedText.Format(Strings.Materializer_CrossDriveReasonFormat, sourceVolume, workspaceVolume)
            : LocalizedText.Format(Strings.Materializer_LinkReasonFormat, sourceVolume);

        return new IOException(LocalizedText.Format(Strings.Materializer_LinkFailureFormat, Environment.NewLine, reason, workspaceVolume));
    }

    private static string GetVolumeDisplayName(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrWhiteSpace(root) ? Strings.Materializer_UnknownVolume : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryCreateHardLink(string targetFile, string existingFile)
    {
        try { return CreateHardLink(targetFile, existingFile, IntPtr.Zero); }
        catch { return false; }
    }

    private static bool TryCreateSymbolicFileLink(string targetFile, string existingFile)
    {
        try { return CreateSymbolicLink(targetFile, existingFile, 0x2); }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

}

internal sealed class WorkspaceBuildStats
{
    private readonly Dictionary<string, WorkspaceFileStat> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkspaceFileKind> _readOnlyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requiredLocalFiles = new(StringComparer.OrdinalIgnoreCase);

    public int FileCount => _files.Count;
    public int LinkedFiles => _files.Values.Count(file => file.Kind == WorkspaceFileKind.HardLink);
    public int SymbolicLinkedFiles => _files.Values.Count(file => file.Kind == WorkspaceFileKind.SymbolicLink);
    public int ProtectedCopies => _files.Values.Count(file => file.Kind == WorkspaceFileKind.LocalCopy);
    public int RequiredLocalFiles => _requiredLocalFiles.Count;
    public int ReadOnlyHandledFiles => _readOnlyFiles.Count;
    public int ReadOnlySymbolicLinkedFiles => _readOnlyFiles.Values.Count(kind => kind == WorkspaceFileKind.SymbolicLink);
    public int ReadOnlyCopiedFiles => _readOnlyFiles.Values.Count(kind => kind == WorkspaceFileKind.LocalCopy);
    public long LogicalSizeBytes => _files.Values.Sum(file => file.Length);
    public long PhysicalSizeBytes => _files.Values.Where(file => file.Kind == WorkspaceFileKind.LocalCopy).Sum(file => file.Length);

    public void Record(string relativePath, WorkspaceFileKind kind, long length)
    {
        _files[relativePath] = new WorkspaceFileStat(kind, length);
        _readOnlyFiles.Remove(relativePath);
        _requiredLocalFiles.Remove(relativePath);
    }

    public void RecordReadOnly(string relativePath, WorkspaceFileKind kind, long length)
    {
        _files[relativePath] = new WorkspaceFileStat(kind, length);
        _readOnlyFiles[relativePath] = kind;
        _requiredLocalFiles.Remove(relativePath);
    }

    public void RecordRequiredLocal(string relativePath, long length)
    {
        _files[relativePath] = new WorkspaceFileStat(WorkspaceFileKind.LocalCopy, length);
        _readOnlyFiles.Remove(relativePath);
        _requiredLocalFiles.Add(relativePath);
    }
}

internal enum WorkspaceFileKind { HardLink, SymbolicLink, LocalCopy }
internal sealed record WorkspaceFileStat(WorkspaceFileKind Kind, long Length);
