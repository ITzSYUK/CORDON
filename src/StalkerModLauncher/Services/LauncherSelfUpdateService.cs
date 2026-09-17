using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed record LauncherUpdateRequest(
    int ParentProcessId,
    string TargetDirectory,
    string StagingDirectory,
    LauncherReleasePackage Package,
    string ReadyEventName);

internal static class LauncherSelfUpdateService
{
    private const string ApplyArgument = "--apply-launcher-update";
    private const string CleanupArgument = "--cleanup-launcher-updater";
    private const string ReadyEventPrefix = @"Local\CORDON-Update-Ready-";
    private const long MaximumUncompressedSize = 512L * 1024 * 1024;
    private const int MaximumFileCount = 32;
    private const long FreeSpaceMargin = 64L * 1024 * 1024;
    private static readonly TimeSpan UpdaterReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] RequiredCommonFiles =
    [
        "StalkerModLauncher.UsvfsX86Host.exe",
        "usvfs_x64.dll",
        "usvfs_proxy_x64.exe",
        "usvfs_x86.dll",
        "usvfs_proxy_x86.exe",
        "LICENSE.txt",
        "THIRD-PARTY-NOTICES.txt",
        "checksums.txt"
    ];

    public static void PrepareAndLaunch(string archivePath, LauncherReleasePackage package)
    {
        var launcherPath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException(Strings.Startup_ExecutablePathMissing));
        var targetDirectory = Path.GetDirectoryName(launcherPath)
            ?? throw new InvalidOperationException(Strings.Update_LauncherDirectoryMissing);
        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CORDON-Update-{Guid.NewGuid():N}");
        var updaterDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CORDON-Updater-{Guid.NewGuid():N}");
        var readyEventName = $"{ReadyEventPrefix}{Guid.NewGuid():N}";

        try
        {
            EnsureSufficientSpaceForUpdate(archivePath, targetDirectory, package);
            ExtractAndValidate(archivePath, stagingDirectory, package);
            Directory.CreateDirectory(updaterDirectory);
            var updaterPath = Path.Combine(updaterDirectory, "CORDON-Updater.exe");
            File.Copy(launcherPath, updaterPath);
            using var readyEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                readyEventName,
                out var createdNew);
            if (!createdNew)
            {
                throw new InvalidOperationException(Strings.Update_SignalFailed);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                WorkingDirectory = updaterDirectory
            };
            startInfo.ArgumentList.Add(ApplyArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(targetDirectory);
            startInfo.ArgumentList.Add(stagingDirectory);
            startInfo.ArgumentList.Add(((int)package).ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(readyEventName);
            using var updater = Process.Start(startInfo)
                ?? throw new InvalidOperationException(Strings.Update_InstallerStartFailed);
            if (!readyEvent.WaitOne(UpdaterReadyTimeout))
            {
                throw new TimeoutException(Strings.Update_AcknowledgeTimeout);
            }
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(updaterDirectory);
            throw;
        }
    }

    public static bool TryParseRequest(string[] arguments, out LauncherUpdateRequest? request)
    {
        request = null;
        if (arguments.Length != 6 ||
            !arguments[0].Equals(ApplyArgument, StringComparison.Ordinal) ||
            !int.TryParse(arguments[1], out var parentProcessId) ||
            parentProcessId <= 0 ||
            !int.TryParse(arguments[4], out var packageValue) ||
            !Enum.IsDefined(typeof(LauncherReleasePackage), packageValue) ||
            !IsReadyEventName(arguments[5]))
        {
            return false;
        }

        try
        {
            request = new LauncherUpdateRequest(
                parentProcessId,
                Path.GetFullPath(arguments[2]),
                Path.GetFullPath(arguments[3]),
                (LauncherReleasePackage)packageValue,
                arguments[5]);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static async Task ApplyAsync(LauncherUpdateRequest request)
    {
        if (!IsOwnedTemporaryDirectory(request.StagingDirectory, "CORDON-Update-"))
        {
            throw new InvalidOperationException(Strings.Update_TempFolderInvalid);
        }

        ValidateStagingDirectory(request.StagingDirectory, request.Package);
        await WaitForParentExitAsync(
            request.ParentProcessId,
            request.TargetDirectory,
            request.ReadyEventName);
        ValidateStagingDirectory(request.StagingDirectory, request.Package);
        ApplyStagedFiles(request.StagingDirectory, request.TargetDirectory, request.Package);

        TryDeleteDirectory(request.StagingDirectory);
        var executablePath = Path.Combine(request.TargetDirectory, GetExecutableName(request.Package));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = request.TargetDirectory
        };
        startInfo.ArgumentList.Add(CleanupArgument);
        startInfo.ArgumentList.Add(Environment.ProcessPath!);
        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException(Strings.Update_RestartFailed);
    }

    internal static async Task WaitForParentExitAsync(
        int parentProcessId,
        string targetDirectory,
        string readyEventName)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                Strings.Update_LauncherExitedEarly,
                ex);
        }

        using (parent)
        {
            var parentPath = parent.MainModule?.FileName
                ?? throw new InvalidOperationException(Strings.Update_ProcessCheckFailed);
            var parentFileName = Path.GetFileName(parentPath);
            if ((!parentFileName.Equals("CORDON.exe", StringComparison.OrdinalIgnoreCase) &&
                 !parentFileName.Equals("CORDON-Standalone.exe", StringComparison.OrdinalIgnoreCase)) ||
                !Path.GetDirectoryName(Path.GetFullPath(parentPath))!
                    .Equals(targetDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Strings.Update_WrongLauncher);
            }

            using var readyEvent = EventWaitHandle.OpenExisting(readyEventName);
            readyEvent.Set();
            await parent.WaitForExitAsync();
        }
    }

    public static void ScheduleUpdaterCleanup(string[] arguments)
    {
        var index = Array.FindIndex(arguments, argument =>
            argument.Equals(CleanupArgument, StringComparison.Ordinal));
        if (index < 0 || index + 1 >= arguments.Length)
        {
            return;
        }

        string updaterPath;
        try
        {
            updaterPath = Path.GetFullPath(arguments[index + 1]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var updaterDirectory = Path.GetDirectoryName(updaterPath);
        if (!Path.GetFileName(updaterPath).Equals("CORDON-Updater.exe", StringComparison.OrdinalIgnoreCase) ||
            updaterDirectory is null ||
            !IsOwnedTemporaryDirectory(updaterDirectory, "CORDON-Updater-"))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                File.Delete(updaterPath);
                Directory.Delete(updaterDirectory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }

    internal static void ExtractAndValidate(
        string archivePath,
        string stagingDirectory,
        LauncherReleasePackage package)
    {
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var files = GetValidatedArchiveFiles(archive);
            foreach (var entry in files)
            {
                entry.ExtractToFile(Path.Combine(stagingDirectory, entry.Name));
            }

            ValidateStagingDirectory(stagingDirectory, package);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, long> CalculateRequiredFreeSpace(
        string targetDirectory,
        string stagingDirectory,
        long stagingBytes,
        long backupBytes,
        long largestReplacementBytes)
    {
        if (stagingBytes < 0 || backupBytes < 0 || largestReplacementBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stagingBytes));
        }

        var requiredByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        AddRequiredSpace(requiredByRoot, targetDirectory, largestReplacementBytes);
        AddRequiredSpace(requiredByRoot, stagingDirectory, checked(stagingBytes + backupBytes));
        foreach (var root in requiredByRoot.Keys.ToArray())
        {
            requiredByRoot[root] = checked(requiredByRoot[root] + FreeSpaceMargin);
        }

        return requiredByRoot;
    }

    internal static void EnsureSpaceForDownload(string destinationDirectory, long archiveBytes)
    {
        if (archiveBytes < 0)
        {
            throw new InvalidDataException(Strings.Update_ArchiveSizeInvalid);
        }

        EnsureAvailableSpace(CalculateRequiredFreeSpace(
            destinationDirectory,
            destinationDirectory,
            stagingBytes: 0,
            backupBytes: 0,
            largestReplacementBytes: archiveBytes));
    }

    private static void EnsureSufficientSpaceForUpdate(
        string archivePath,
        string targetDirectory,
        LauncherReleasePackage package)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = GetValidatedArchiveFiles(archive);
        var stagingBytes = files.Sum(entry => entry.Length);
        var backupBytes = files
            .Select(entry => Path.Combine(targetDirectory, entry.Name))
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
        var obsoleteExecutable = Path.Combine(
            targetDirectory,
            GetExecutableName(package == LauncherReleasePackage.Minimal
                ? LauncherReleasePackage.Standalone
                : LauncherReleasePackage.Minimal));
        if (File.Exists(obsoleteExecutable))
        {
            backupBytes = checked(backupBytes + new FileInfo(obsoleteExecutable).Length);
        }

        EnsureAvailableSpace(CalculateRequiredFreeSpace(
            targetDirectory,
            Path.GetTempPath(),
            stagingBytes,
            backupBytes,
            files.Max(entry => entry.Length)));
    }

    private static ZipArchiveEntry[] GetValidatedArchiveFiles(ZipArchive archive)
    {
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0 || files.Length > MaximumFileCount ||
            files.Sum(entry => entry.Length) > MaximumUncompressedSize)
        {
            throw new InvalidDataException(Strings.Update_ArchiveInvalid);
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Replace('\\', '/').StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(Strings.Update_DataFolderForbidden);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new InvalidDataException(Strings.Update_FoldersForbidden);
            }

            if (!entry.FullName.Equals(entry.Name, StringComparison.Ordinal) ||
                entry.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / entry.CompressedLength > 200))
            {
                throw new InvalidDataException(LocalizedText.Format(Strings.Update_EntryInvalidFormat, entry.FullName));
            }
        }

        return files;
    }

    private static void EnsureAvailableSpace(IReadOnlyDictionary<string, long> requiredByRoot)
    {
        foreach (var (root, requiredBytes) in requiredByRoot)
        {
            var availableBytes = new DriveInfo(root).AvailableFreeSpace;
            if (availableBytes < requiredBytes)
            {
                throw new IOException(
                    LocalizedText.Format(Strings.Update_NoSpaceFormat, root, requiredBytes / 1024 / 1024, availableBytes / 1024 / 1024));
            }
        }
    }

    private static void AddRequiredSpace(
        Dictionary<string, long> requiredByRoot,
        string directory,
        long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))
            ?? throw new InvalidOperationException(Strings.Update_VolumeMissing);
        requiredByRoot.TryGetValue(root, out var currentBytes);
        requiredByRoot[root] = checked(currentBytes + bytes);
    }

    private static bool IsReadyEventName(string value) =>
        value.StartsWith(ReadyEventPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value[ReadyEventPrefix.Length..], "N", out _);

    internal static void ValidateStagingDirectory(
        string stagingDirectory,
        LauncherReleasePackage package)
    {
        var files = Directory.GetFiles(stagingDirectory, "*", SearchOption.TopDirectoryOnly);
        if (Directory.GetDirectories(stagingDirectory).Length != 0)
        {
            throw new InvalidDataException(Strings.Update_FoldersForbidden);
        }

        foreach (var requiredFile in RequiredCommonFiles.Append(GetExecutableName(package)))
        {
            if (!File.Exists(Path.Combine(stagingDirectory, requiredFile)))
            {
                throw new InvalidDataException(LocalizedText.Format(Strings.Update_RequiredFileMissingFormat, requiredFile));
            }
        }

        var otherExecutable = GetExecutableName(package == LauncherReleasePackage.Minimal
            ? LauncherReleasePackage.Standalone
            : LauncherReleasePackage.Minimal);
        if (File.Exists(Path.Combine(stagingDirectory, otherExecutable)))
        {
            throw new InvalidDataException(Strings.Update_WrongBuildType);
        }

        var checksums = ReadChecksums(Path.Combine(stagingDirectory, "checksums.txt"));
        var payloadFiles = files
            .Where(path => !Path.GetFileName(path).Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (checksums.Count != payloadFiles.Length)
        {
            throw new InvalidDataException(Strings.Update_ChecksumsIncomplete);
        }

        foreach (var path in payloadFiles)
        {
            var fileName = Path.GetFileName(path);
            if (!checksums.TryGetValue(fileName, out var expectedHash) ||
                !GetSha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(LocalizedText.Format(Strings.Update_FileChecksumMismatchFormat, fileName));
            }
        }
    }

    internal static void ApplyStagedFiles(
        string stagingDirectory,
        string targetDirectory,
        LauncherReleasePackage package)
    {
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"CORDON-Backup-{Guid.NewGuid():N}");
        var changedFiles = new List<(string Target, string? Backup)>();
        var keepBackup = false;
        Directory.CreateDirectory(backupDirectory);

        try
        {
            foreach (var source in Directory.GetFiles(stagingDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(source);
                if (fileName.Equals("Data", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(Strings.Update_DataChangeForbidden);
                }

                var target = Path.Combine(targetDirectory, fileName);
                var backup = File.Exists(target) ? Path.Combine(backupDirectory, fileName) : null;
                if (backup is not null)
                {
                    File.Copy(target, backup);
                }

                var replacement = target + $".cordon-new-{Guid.NewGuid():N}";
                try
                {
                    File.Copy(source, replacement);
                    File.Move(replacement, target, overwrite: true);
                }
                finally
                {
                    File.Delete(replacement);
                }

                changedFiles.Add((target, backup));
            }

            var obsoleteExecutable = Path.Combine(
                targetDirectory,
                GetExecutableName(package == LauncherReleasePackage.Minimal
                    ? LauncherReleasePackage.Standalone
                    : LauncherReleasePackage.Minimal));
            if (File.Exists(obsoleteExecutable))
            {
                var backup = Path.Combine(backupDirectory, Path.GetFileName(obsoleteExecutable));
                File.Copy(obsoleteExecutable, backup);
                File.Delete(obsoleteExecutable);
                changedFiles.Add((obsoleteExecutable, backup));
            }
        }
        catch (Exception updateError)
        {
            Exception? rollbackError = null;
            foreach (var (target, backup) in changedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    if (backup is null)
                    {
                        File.Delete(target);
                    }
                    else
                    {
                        File.Copy(backup, target, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackError ??= ex;
                }
            }

            if (rollbackError is not null)
            {
                keepBackup = true;
                throw new AggregateException(
                    LocalizedText.Format(Strings.Update_RollbackIncompleteFormat, backupDirectory),
                    updateError,
                    rollbackError);
            }

            throw;
        }
        finally
        {
            if (!keepBackup)
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadChecksums(string checksumPath)
    {
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(checksumPath))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 ||
                !line[..separator].All(Uri.IsHexDigit) ||
                Path.GetFileName(line[(separator + 2)..]) != line[(separator + 2)..] ||
                !checksums.TryAdd(line[(separator + 2)..], line[..separator]))
            {
                throw new InvalidDataException(Strings.Update_ChecksumFileInvalid);
            }
        }

        return checksums;
    }

    internal static string GetExecutableName(LauncherReleasePackage package) => package switch
    {
        LauncherReleasePackage.Minimal => "CORDON.exe",
        LauncherReleasePackage.Standalone => "CORDON-Standalone.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(package), package, null)
    };

    private static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsOwnedTemporaryDirectory(string path, string prefix)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(fullPath);
        return parent is not null &&
               parent.Equals(
                   Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
