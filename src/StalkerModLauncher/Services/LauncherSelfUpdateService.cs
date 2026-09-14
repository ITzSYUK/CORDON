using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace StalkerModLauncher.Services;

internal sealed record LauncherUpdateRequest(
    int ParentProcessId,
    string TargetDirectory,
    string StagingDirectory,
    LauncherReleasePackage Package);

internal static class LauncherSelfUpdateService
{
    private const string ApplyArgument = "--apply-launcher-update";
    private const string CleanupArgument = "--cleanup-launcher-updater";
    private const long MaximumUncompressedSize = 512L * 1024 * 1024;
    private const int MaximumFileCount = 32;
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
        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CORDON-Update-{Guid.NewGuid():N}");
        var updaterDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CORDON-Updater-{Guid.NewGuid():N}");

        try
        {
            ExtractAndValidate(archivePath, stagingDirectory, package);
            Directory.CreateDirectory(updaterDirectory);
            var updaterPath = Path.Combine(updaterDirectory, "CORDON-Updater.exe");
            File.Copy(
                Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь лаунчера."),
                updaterPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                WorkingDirectory = updaterDirectory
            };
            startInfo.ArgumentList.Add(ApplyArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(Path.GetFullPath(AppContext.BaseDirectory));
            startInfo.ArgumentList.Add(stagingDirectory);
            startInfo.ArgumentList.Add(((int)package).ToString(CultureInfo.InvariantCulture));
            using var updater = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить установщик обновления.");
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
        if (arguments.Length != 5 ||
            !arguments[0].Equals(ApplyArgument, StringComparison.Ordinal) ||
            !int.TryParse(arguments[1], out var parentProcessId) ||
            parentProcessId <= 0 ||
            !int.TryParse(arguments[4], out var packageValue) ||
            !Enum.IsDefined(typeof(LauncherReleasePackage), packageValue))
        {
            return false;
        }

        try
        {
            request = new LauncherUpdateRequest(
                parentProcessId,
                Path.GetFullPath(arguments[2]),
                Path.GetFullPath(arguments[3]),
                (LauncherReleasePackage)packageValue);
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
            throw new InvalidOperationException("Временная папка обновления недопустима.");
        }

        using var parent = Process.GetProcessById(request.ParentProcessId);
        var parentPath = parent.MainModule?.FileName
            ?? throw new InvalidOperationException("Не удалось проверить запущенный лаунчер.");
        var parentFileName = Path.GetFileName(parentPath);
        if (!parentFileName.Equals("CORDON.exe", StringComparison.OrdinalIgnoreCase) &&
            !parentFileName.Equals("CORDON-Standalone.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(Path.GetFullPath(parentPath))!
                .Equals(request.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Запрос обновления создан не текущим лаунчером.");
        }

        ValidateStagingDirectory(request.StagingDirectory, request.Package);
        await parent.WaitForExitAsync();
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
            ?? throw new InvalidOperationException("Не удалось запустить обновлённый лаунчер.");
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
            var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
            if (files.Length == 0 || files.Length > MaximumFileCount ||
                files.Sum(entry => entry.Length) > MaximumUncompressedSize)
            {
                throw new InvalidDataException("Архив обновления имеет недопустимый размер или состав.");
            }

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Replace('\\', '/').StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Архив обновления не может содержать папку Data.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    throw new InvalidDataException("Архив обновления не должен содержать папки.");
                }

                if (!entry.FullName.Equals(entry.Name, StringComparison.Ordinal) ||
                    entry.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                    entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / entry.CompressedLength > 200))
                {
                    throw new InvalidDataException($"Недопустимая запись в архиве обновления: {entry.FullName}");
                }

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

    internal static void ValidateStagingDirectory(
        string stagingDirectory,
        LauncherReleasePackage package)
    {
        var files = Directory.GetFiles(stagingDirectory, "*", SearchOption.TopDirectoryOnly);
        if (Directory.GetDirectories(stagingDirectory).Length != 0)
        {
            throw new InvalidDataException("Архив обновления не должен содержать папки.");
        }

        foreach (var requiredFile in RequiredCommonFiles.Append(GetExecutableName(package)))
        {
            if (!File.Exists(Path.Combine(stagingDirectory, requiredFile)))
            {
                throw new InvalidDataException($"В архиве обновления отсутствует {requiredFile}.");
            }
        }

        var otherExecutable = GetExecutableName(package == LauncherReleasePackage.Minimal
            ? LauncherReleasePackage.Standalone
            : LauncherReleasePackage.Minimal);
        if (File.Exists(Path.Combine(stagingDirectory, otherExecutable)))
        {
            throw new InvalidDataException("Архив содержит исполняемый файл другого типа сборки.");
        }

        var checksums = ReadChecksums(Path.Combine(stagingDirectory, "checksums.txt"));
        var payloadFiles = files
            .Where(path => !Path.GetFileName(path).Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (checksums.Count != payloadFiles.Length)
        {
            throw new InvalidDataException("Контрольные суммы не покрывают все файлы обновления.");
        }

        foreach (var path in payloadFiles)
        {
            var fileName = Path.GetFileName(path);
            if (!checksums.TryGetValue(fileName, out var expectedHash) ||
                !GetSha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Контрольная сумма файла {fileName} не совпадает.");
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
                    throw new InvalidDataException("Обновление не может изменять Data.");
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
                    $"Не удалось полностью откатить обновление. Резервные копии сохранены в {backupDirectory}",
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
                throw new InvalidDataException("Файл контрольных сумм имеет недопустимый формат.");
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
