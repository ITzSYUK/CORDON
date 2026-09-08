using System.Runtime.InteropServices;
using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

internal static class WorkspaceMaterializer
{
    public static void DeleteWorkspaceContents(
        string workspaceDirectory,
        string allowedRoot,
        Func<WorkspaceSourceSnapshot> snapshotFactory,
        IProgress<string>? progress = null)
    {
        try
        {
            FileSystemSafety.DeleteDirectoryContents(workspaceDirectory, allowedRoot);
            return;
        }
        catch (Exception ex) when (Directory.Exists(workspaceDirectory) &&
                                   (ex is UnauthorizedAccessException or IOException))
        {
            // Old workspace versions could contain read-only hard links. NTFS shares
            // attributes between hard links, so we temporarily unlock only the
            // workspace entries and restore the source attributes immediately after.
            progress?.Report("Обнаружены защищённые ссылки от старой сборки. Подготавливаю безопасную очистку workspace...");
        }

        var sourceAttributes = CaptureReadOnlySourceAttributes(snapshotFactory());
        try
        {
            var releasedFiles = ClearReadOnlyWorkspaceAttributes(workspaceDirectory);
            progress?.Report($"Освобождено защищённых ссылок старого workspace: {releasedFiles:N0}. Атрибуты исходных файлов будут восстановлены.");
            FileSystemSafety.DeleteDirectoryContents(workspaceDirectory, allowedRoot);
        }
        finally
        {
            RestoreSourceAttributes(sourceAttributes);
        }
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

        progress.Report("Проверка symbolic link для файлов на других дисках...");
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
                progress.Report($"Подключено файлов базовой игры: {fileCount:N0}...");
            }
        }
    }

    public static void ApplyMod(
        string workspaceRoot,
        ModEntry mod,
        DirectorySnapshot source,
        IProgress<string> progress,
        WorkspaceBuildStats stats,
        CancellationToken cancellationToken)
    {
        progress.Report($"Подключение мода: {mod.Name}");
        foreach (var relativePath in source.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(workspaceRoot, relativePath));
        }

        foreach (var file in source.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mod.ExcludedFiles.Any(excluded =>
                    NormalizeRelativePath(excluded).Equals(
                        NormalizeRelativePath(file.RelativePath),
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            FileSystemSafety.EnsureRelativePath(file.RelativePath, "Mod file");
            var targetFile = Path.Combine(workspaceRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }

            LinkFile(file.FullPath, targetFile, file.RelativePath, stats, IsAnomalyLauncherConfiguration(file.RelativePath));
        }

        progress.Report($"Мод подключён: {mod.Name}. Файлов: {source.Files.Count:N0}; папок: {source.Directories.Count:N0}.");
        if (source.Files.Count == 0)
        {
            progress.Report($"Предупреждение: в моде «{mod.Name}» не найдено файлов. Проверьте, что выбрана корневая папка мода, а не пустая внешняя папка.");
        }
    }

    public static void ReplaceFile(
        string sourceFile,
        string workspaceRoot,
        string relativePath,
        WorkspaceBuildStats stats)
    {
        FileSystemSafety.EnsureRelativePath(relativePath, "Workspace file");
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

    private static List<SourceFileAttributes> CaptureReadOnlySourceAttributes(WorkspaceSourceSnapshot snapshot)
    {
        var sourceFiles = new[] { snapshot.Game }
            .Concat(snapshot.Mods.Values)
            .SelectMany(directory => directory.Files)
            .GroupBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var attributes = new List<SourceFileAttributes>();

        foreach (var sourceFile in sourceFiles)
        {
            if (!File.Exists(sourceFile.FullPath))
            {
                continue;
            }

            var sourceAttributes = File.GetAttributes(sourceFile.FullPath);
            if ((sourceAttributes & FileAttributes.ReadOnly) != 0)
            {
                attributes.Add(new SourceFileAttributes(sourceFile.FullPath, sourceAttributes));
            }
        }

        return attributes;
    }

    private static int ClearReadOnlyWorkspaceAttributes(string workspaceDirectory)
    {
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
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                releasedFiles++;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(workspaceDirectory, "*", options))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
            }
        }

        return releasedFiles;
    }

    private static void RestoreSourceAttributes(IEnumerable<SourceFileAttributes> sourceAttributes)
    {
        foreach (var source in sourceAttributes)
        {
            if (File.Exists(source.FullPath))
            {
                File.SetAttributes(source.FullPath, source.Attributes);
            }
        }
    }

    private static IOException CreateLinkFailureException(string sourceFile, string targetFile)
    {
        var sourceVolume = GetVolumeDisplayName(sourceFile);
        var workspaceVolume = GetVolumeDisplayName(targetFile);
        var reason = !string.Equals(sourceVolume, workspaceVolume, StringComparison.OrdinalIgnoreCase)
            ? $"Файлы мода находятся на диске {sourceVolume}, а workspace создаётся на диске {workspaceVolume}. Windows не разрешила создать символическую ссылку между ними."
            : $"Windows не разрешила создать ссылку на диске {sourceVolume}. Возможно, диск не использует NTFS или у лаунчера недостаточно прав.";

        return new IOException(
            "Не удалось подключить файлы мода к профилю без копирования." + Environment.NewLine +
            Environment.NewLine + reason + Environment.NewLine + Environment.NewLine +
            "Чтобы исправить проблему:" + Environment.NewLine +
            "1. Включите «Режим разработчика» в параметрах Windows: Система → Для разработчиков." + Environment.NewLine +
            "2. Или запустите лаунчер от имени администратора." + Environment.NewLine +
            $"3. Или перенесите мод на диск {workspaceVolume}." + Environment.NewLine + Environment.NewLine +
            "Сборка остановлена. Файлы игры и мода не изменены.");
    }

    private static string GetVolumeDisplayName(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrWhiteSpace(root) ? "неизвестный" : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

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

    private sealed record SourceFileAttributes(string FullPath, FileAttributes Attributes);
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
