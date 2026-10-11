using SharpCompress.Archives;
using SharpCompress.Common;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services.Fomod;

namespace StalkerModLauncher.Services;

public sealed record ModArchiveInstallResult(
    string ModName,
    string ModPath,
    int FileCount,
    long ExtractedBytes,
    bool DatabaseArchivesRelocated,
    bool FomodApplied = false,
    string FomodModuleName = "");

public sealed record ModArchiveInstallDestination(
    string ModName,
    string PackageDirectoryName,
    string PackagePath,
    bool RequiresConfirmation);

public enum ModArchiveInstallStage
{
    Inspecting,
    Extracting,
    Finalizing,
    Copying
}

/// <summary>
/// Interactive FOMOD choice request. The callback runs on a background thread
/// and must marshal to the UI thread itself; returning null cancels the install.
/// </summary>
public sealed record FomodChoiceRequest(FomodModule Module, string PackageRoot);

public sealed record ModArchiveInstallProgress(
    ModArchiveInstallStage Stage,
    int ExtractedFileCount,
    long ExtractedBytes,
    long? TotalBytes);

public static class ModArchiveInstaller
{
    private const int MaximumEntryCount = 250_000;
    private const long FreeSpaceReserve = 256L * 1024 * 1024;
    private const int MaximumContentRootDepth = 6;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".fomod",
        ".7z",
        ".rar"
    };

    private static readonly HashSet<string> ContentDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata",
        "bin",
        "bin_x64",
        "db",
        "gamedata",
        "patches",
        "userdata",
        "_appdata_"
    };

    public static Task<ModArchiveInstallResult> InstallAsync(
        string archivePath,
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => InstallCore(archivePath, installRoot, progress: null, fomodChooser: null, cancellationToken),
            cancellationToken);
    }

    public static Task<ModArchiveInstallResult> InstallAsync(
        string archivePath,
        string installRoot,
        string packageDirectoryName,
        IProgress<ModArchiveInstallProgress> progress,
        CancellationToken cancellationToken = default) =>
        InstallAsync(archivePath, installRoot, packageDirectoryName, progress, null, cancellationToken);

    public static Task<ModArchiveInstallResult> InstallAsync(
        string archivePath,
        string installRoot,
        string packageDirectoryName,
        IProgress<ModArchiveInstallProgress> progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectoryName);
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(
            () => InstallCore(archivePath, installRoot, packageDirectoryName, progress, fomodChooser, cancellationToken),
            cancellationToken);
    }

    public static ModArchiveInstallDestination PlanInstall(string archivePath, string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException(Strings.Archive_NotFound, fullArchivePath);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(fullArchivePath)))
        {
            throw new InvalidDataException(Strings.Archive_SupportedFormats);
        }

        var fullInstallRoot = Path.GetFullPath(installRoot);
        var modName = FileSystemSafety.SanitizeName(Path.GetFileNameWithoutExtension(fullArchivePath));
        return PlanDestination(fullInstallRoot, modName);
    }

    public static ModArchiveInstallDestination PlanFomodFolderInstall(string folderPath, string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        var source = Path.GetFullPath(folderPath);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Fomod_FolderNotFoundFormat, source));
        }
        var target = Path.GetFullPath(installRoot);
        if (FileSystemSafety.IsDirectoryInside(target, source))
        {
            throw new InvalidOperationException(Strings.Fomod_DestinationInsideSource);
        }
        return PlanDestination(target, FileSystemSafety.SanitizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(source))));
    }

    private static ModArchiveInstallDestination PlanDestination(string fullInstallRoot, string modName)
    {
        var preferredPath = Path.Combine(fullInstallRoot, modName);
        var requiresConfirmation = Directory.Exists(preferredPath) || File.Exists(preferredPath);
        var packageDirectoryName = requiresConfirmation
            ? GetUniquePackageDirectoryName(fullInstallRoot, modName, startSuffix: 1)
            : modName;

        return new ModArchiveInstallDestination(
            modName,
            packageDirectoryName,
            Path.Combine(fullInstallRoot, packageDirectoryName),
            requiresConfirmation);
    }

    public static Task<ModArchiveInstallResult> InstallFomodFolderAsync(
        string folderPath,
        string installRoot,
        IProgress<ModArchiveInstallProgress> progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser = null,
        string? packageDirectoryName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(() => InstallFomodFolderCore(folderPath, installRoot, progress, fomodChooser, packageDirectoryName, cancellationToken), cancellationToken);
    }

    private static ModArchiveInstallResult InstallFomodFolderCore(
        string folderPath,
        string installRoot,
        IProgress<ModArchiveInstallProgress> progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        string? packageDirectoryName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = PlanFomodFolderInstall(folderPath, installRoot);
        var fullSource = Path.GetFullPath(folderPath);
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var location = FomodDetector.TryLocate(fullSource)
            ?? throw new InvalidDataException(Strings.Fomod_ConfigNotFound);
        if (location.ConfigPath is null) throw new InvalidDataException(Strings.Archive_FomodCSharpUnsupported);
        FomodMaterializer.EnsureNoLinks(fullSource, location.ConfigPath);
        var module = FomodParser.Parse(location.ConfigPath);
        var selection = fomodChooser is null ? FomodSelector.CreateDefaults(module)
            : fomodChooser(new FomodChoiceRequest(module, location.PackageRoot));
        if (selection is null) throw new OperationCanceledException();
        cancellationToken.ThrowIfCancellationRequested();
        var files = FomodSelector.ResolveFiles(module, selection);
        var stagingPath = Path.Combine(fullInstallRoot, $".installing-{Guid.NewGuid():N}");
        try
        {
            progress.Report(new ModArchiveInstallProgress(ModArchiveInstallStage.Copying, 0, 0, null));
            var count = FomodMaterializer.CopyToStaging(location.PackageRoot, stagingPath, fullInstallRoot, files, cancellationToken);
            var bytes = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            progress.Report(new ModArchiveInstallProgress(ModArchiveInstallStage.Finalizing, count, bytes, bytes));
            var databaseArchivesRelocated = RelocateLooseDatabaseArchives(stagingPath);
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = GetRequestedPackagePath(fullInstallRoot, packageDirectoryName ?? destination.PackageDirectoryName);
            Directory.Move(stagingPath, packagePath);
            return new ModArchiveInstallResult(destination.ModName, packagePath, count, bytes, databaseArchivesRelocated, true, module.Name);
        }
        catch
        {
            if (Directory.Exists(stagingPath)) FileSystemSafety.DeleteDirectoryContents(stagingPath, fullInstallRoot);
            throw;
        }
    }

    public static Task<ModArchiveInstallResult> InstallAsync(
        string archivePath,
        string installRoot,
        IProgress<ModArchiveInstallProgress> progress,
        CancellationToken cancellationToken = default) =>
        InstallAsync(archivePath, installRoot, progress, null, cancellationToken);

    public static Task<ModArchiveInstallResult> InstallAsync(
        string archivePath,
        string installRoot,
        IProgress<ModArchiveInstallProgress> progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(
            () => InstallCore(archivePath, installRoot, progress, fomodChooser, cancellationToken),
            cancellationToken);
    }

    private static ModArchiveInstallResult InstallCore(
        string archivePath,
        string installRoot,
        IProgress<ModArchiveInstallProgress>? progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        CancellationToken cancellationToken)
    {
        return InstallCore(archivePath, installRoot, packageDirectoryName: null, progress, fomodChooser, cancellationToken);
    }

    private static ModArchiveInstallResult InstallCore(
        string archivePath,
        string installRoot,
        string? packageDirectoryName,
        IProgress<ModArchiveInstallProgress>? progress,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException(Strings.Archive_NotFound, fullArchivePath);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(fullArchivePath)))
        {
            throw new InvalidDataException(Strings.Archive_SupportedFormats);
        }

        var fullInstallRoot = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(fullInstallRoot);

        var stagingPath = Path.Combine(fullInstallRoot, $".installing-{Guid.NewGuid():N}");
        FileSystemSafety.EnsureDirectoryInside(stagingPath, fullInstallRoot);
        Directory.CreateDirectory(stagingPath);

        try
        {
            progress?.Report(new ModArchiveInstallProgress(
                ModArchiveInstallStage.Inspecting,
                ExtractedFileCount: 0,
                ExtractedBytes: 0,
                TotalBytes: null));

            var extraction = ExtractArchive(fullArchivePath, stagingPath, progress, cancellationToken);
            progress?.Report(new ModArchiveInstallProgress(
                ModArchiveInstallStage.Finalizing,
                extraction.FileCount,
                extraction.ExtractedBytes,
                extraction.ExtractedBytes));
            // Install destination (profile ModInstallPath or the default Mods folder)
            // is resolved by the caller; FOMOD only changes which files from the
            // archive are materialized, never where the mod is placed.
            var (fileCount, fomodApplied, fomodModuleName) =
                ApplyFomodDefaults(stagingPath, fullInstallRoot, extraction.FileCount, fomodChooser, cancellationToken);
            var contentRoot = fomodApplied ? stagingPath : FindContentRoot(stagingPath);
            var databaseArchivesRelocated = RelocateLooseDatabaseArchives(contentRoot);
            var contentRootRelativePath = Path.GetRelativePath(stagingPath, contentRoot);
            var modName = FileSystemSafety.SanitizeName(Path.GetFileNameWithoutExtension(fullArchivePath));
            var packagePath = packageDirectoryName is null
                ? GetUniquePackagePath(fullInstallRoot, modName)
                : GetRequestedPackagePath(fullInstallRoot, packageDirectoryName);

            Directory.Move(stagingPath, packagePath);
            var installedContentRoot = contentRootRelativePath == "."
                ? packagePath
                : Path.Combine(packagePath, contentRootRelativePath);

            return new ModArchiveInstallResult(
                modName,
                installedContentRoot,
                fileCount,
                extraction.ExtractedBytes,
                databaseArchivesRelocated,
                fomodApplied,
                fomodModuleName);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                FileSystemSafety.DeleteDirectoryContents(stagingPath, fullInstallRoot);
            }

            throw;
        }
    }

    /// <summary>
    /// Detects a FOMOD script in the extracted staging content and materializes
    /// the chosen options (interactive wizard when a chooser is provided,
    /// Required/Recommended defaults otherwise). Plain archives are left
    /// untouched. C# scripts are rejected: arbitrary code execution is out of
    /// scope, install such mods manually or via Mod Organizer 2.
    /// A null chooser result cancels the install.
    /// </summary>
    private static (int FileCount, bool FomodApplied, string FomodModuleName) ApplyFomodDefaults(
        string stagingPath,
        string installRoot,
        int extractedFileCount,
        Func<FomodChoiceRequest, FomodSelector.Selection?>? fomodChooser,
        CancellationToken cancellationToken)
    {
        var location = FomodDetector.TryLocate(stagingPath);
        if (location is null)
        {
            return (extractedFileCount, false, string.Empty);
        }

        if (location.ConfigPath is null)
        {
            throw new InvalidDataException(Strings.Archive_FomodCSharpUnsupported);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var module = FomodParser.Parse(location.ConfigPath);
        var selection = fomodChooser is null
            ? FomodSelector.CreateDefaults(module)
            : fomodChooser(new FomodChoiceRequest(module, location.PackageRoot));
        if (selection is null)
        {
            throw new OperationCanceledException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var files = FomodSelector.ResolveFiles(module, selection);
        var materialized = FomodMaterializer.ApplyToStaging(
            location.PackageRoot,
            stagingPath,
            installRoot,
            files,
            cancellationToken);
        return (materialized, true, module.Name);
    }

    private static ExtractionResult ExtractArchive(
        string archivePath,
        string stagingPath,
        IProgress<ModArchiveInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var state = new ExtractionState(
            GetMaximumExtractedBytes(stagingPath),
            GetTotalUncompressedBytes(archive),
            progress);
        state.Report(ModArchiveInstallStage.Extracting, force: true);

        if (archive.Type == ArchiveType.SevenZip || archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                using var input = reader.OpenEntryStream();
                ExtractEntry(
                    stagingPath,
                    reader.Entry.Key,
                    reader.Entry.IsEncrypted && reader.Entry.Size > 0,
                    input,
                    state,
                    cancellationToken);
            }
        }
        else
        {
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = entry.OpenEntryStream();
                ExtractEntry(
                    stagingPath,
                    entry.Key,
                    entry.IsEncrypted && entry.Size > 0,
                    input,
                    state,
                    cancellationToken);
            }
        }

        if (state.FileCount == 0)
        {
            throw new InvalidDataException(Strings.Archive_Empty);
        }

        state.Report(ModArchiveInstallStage.Extracting, force: true);

        return new ExtractionResult(state.FileCount, state.ExtractedBytes);
    }

    private static void ExtractEntry(
        string stagingPath,
        string? key,
        bool isEncrypted,
        Stream input,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        if (isEncrypted)
        {
            throw new InvalidDataException(Strings.Archive_PasswordProtected);
        }

        state.FileCount++;
        if (state.FileCount > MaximumEntryCount)
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Archive_TooManyFilesFormat, MaximumEntryCount));
        }

        var entryKey = key ?? string.Empty;
        var destinationPath = ResolveEntryPath(stagingPath, entryKey);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException(LocalizedText.Format(Strings.Archive_InvalidEntryPathFormat, entryKey));
        Directory.CreateDirectory(destinationDirectory);

        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        CopyEntry(input, output, state, cancellationToken);
        state.Report(ModArchiveInstallStage.Extracting);
    }

    private static void CopyEntry(
        Stream input,
        Stream output,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        var buffer = state.CopyBuffer;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.AddExtractedBytes(read);
            output.Write(buffer, 0, read);
            state.Report(ModArchiveInstallStage.Extracting);
        }

    }

    private static string ResolveEntryPath(string stagingPath, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            throw new InvalidDataException(Strings.Archive_EntryPathMissing);
        }

        var normalized = entryKey.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.StartsWith('/') ||
            segments.Length == 0 ||
            segments.Any(IsUnsafePathSegment))
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Archive_UnsafeEntryPathFormat, entryKey));
        }

        var relativePath = Path.Combine(segments);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Archive_UnsafeEntryPathFormat, entryKey));
        }

        var destinationPath = Path.GetFullPath(Path.Combine(stagingPath, relativePath));
        if (!FileSystemSafety.IsDirectoryInside(destinationPath, stagingPath))
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Archive_EntryOutsideDestinationFormat, entryKey));
        }

        return destinationPath;
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        return segment is "." or ".." ||
               segment.EndsWith(' ') ||
               segment.EndsWith('.') ||
               segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
    }

    private static string FindContentRoot(string stagingPath)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((stagingPath, 0));
        var candidates = new List<(string Path, int Depth)>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (LooksLikeXRayContentRoot(current.Path))
            {
                candidates.Add(current);
                continue;
            }

            if (current.Depth >= MaximumContentRootDepth)
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(current.Path))
            {
                queue.Enqueue((directory, current.Depth + 1));
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(Strings.Archive_XrayRootMissing);
        }

        var shallowestDepth = candidates.Min(candidate => candidate.Depth);
        var shallowest = candidates.Where(candidate => candidate.Depth == shallowestDepth).ToArray();
        if (shallowest.Length != 1)
        {
            throw new InvalidDataException(Strings.Archive_MultipleRoots);
        }

        return shallowest[0].Path;
    }

    private static bool LooksLikeXRayContentRoot(string path)
    {
        if (Directory.EnumerateDirectories(path)
            .Any(directory => IsContentDirectory(Path.GetFileName(directory))))
        {
            return true;
        }

        return Directory.EnumerateFiles(path).Any(file =>
            Path.GetFileName(file).Equals("fsgame.ltx", StringComparison.OrdinalIgnoreCase) ||
            IsDatabaseArchive(file));
    }

    private static bool IsContentDirectory(string directoryName) =>
        ContentDirectories.Contains(directoryName) ||
        directoryName.StartsWith("bin_", StringComparison.OrdinalIgnoreCase);

    private static bool RelocateLooseDatabaseArchives(string contentRoot)
    {
        var looseArchives = Directory.EnumerateFiles(contentRoot)
            .Where(IsDatabaseArchive)
            .ToArray();
        if (looseArchives.Length == 0)
        {
            return false;
        }

        var archiveDirectory = Path.Combine(contentRoot, "db", "mods");
        Directory.CreateDirectory(archiveDirectory);
        foreach (var archive in looseArchives)
        {
            File.Move(archive, Path.Combine(archiveDirectory, Path.GetFileName(archive)), overwrite: false);
        }

        return true;
    }

    private static bool IsDatabaseArchive(string path) =>
        Path.GetExtension(path).StartsWith(".db", StringComparison.OrdinalIgnoreCase);

    private static string GetUniquePackagePath(string installRoot, string modName) =>
        Path.Combine(installRoot, GetUniquePackageDirectoryName(installRoot, modName, startSuffix: 0));

    private static string GetRequestedPackagePath(string installRoot, string packageDirectoryName)
    {
        var sanitized = FileSystemSafety.SanitizeName(packageDirectoryName);
        if (!packageDirectoryName.Equals(sanitized, StringComparison.Ordinal))
        {
            throw new InvalidDataException(Strings.Archive_InvalidInstallFolder);
        }

        var packagePath = Path.GetFullPath(Path.Combine(installRoot, packageDirectoryName));
        if (!FileSystemSafety.IsDirectoryInside(packagePath, installRoot) ||
            Directory.Exists(packagePath) ||
            File.Exists(packagePath))
        {
            throw new IOException(LocalizedText.Format(Strings.Archive_InstallFolderUnavailableFormat, packagePath));
        }

        return packagePath;
    }

    private static string GetUniquePackageDirectoryName(string installRoot, string modName, int startSuffix)
    {
        for (var suffix = startSuffix; suffix < 10_000; suffix++)
        {
            var directoryName = suffix == 0 ? modName : $"{modName}({suffix})";
            var candidate = Path.Combine(installRoot, directoryName);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return directoryName;
            }
        }

        throw new IOException(LocalizedText.Format(Strings.Archive_AllocateFolderFailedFormat, modName));
    }

    private static long GetMaximumExtractedBytes(string destinationPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var available = new DriveInfo(root).AvailableFreeSpace - FreeSpaceReserve;
                if (available <= 0)
                {
                    throw new IOException(Strings.Archive_NotEnoughSpace);
                }

                return available;
            }
        }
        catch (ArgumentException)
        {
            // UNC and virtual paths may not expose DriveInfo data.
        }

        return long.MaxValue;
    }

    private static long? GetTotalUncompressedBytes(IArchive archive)
    {
        try
        {
            var totalBytes = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Aggregate(0L, (total, entry) => checked(total + entry.Size));
            return totalBytes > 0 ? totalBytes : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private sealed record ExtractionResult(int FileCount, long ExtractedBytes);

    private sealed class ExtractionState(
        long maximumBytes,
        long? totalBytes,
        IProgress<ModArchiveInstallProgress>? progress)
    {
        private const long MinimumReportByteInterval = 1024 * 1024;
        private const long MinimumReportTimeIntervalMilliseconds = 100;
        private long _lastReportedBytes;
        private long _lastReportTimestamp = Environment.TickCount64;

        public long MaximumBytes { get; } = maximumBytes;
        public long? TotalBytes { get; } = totalBytes;
        public byte[] CopyBuffer { get; } = new byte[128 * 1024];
        public int FileCount { get; set; }
        public long ExtractedBytes { get; private set; }

        public void AddExtractedBytes(int byteCount)
        {
            ExtractedBytes = checked(ExtractedBytes + byteCount);
            if (ExtractedBytes > MaximumBytes)
            {
                throw new IOException(Strings.Archive_NotEnoughSafeSpace);
            }
        }

        public void Report(ModArchiveInstallStage stage, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (!force &&
                ExtractedBytes - _lastReportedBytes < MinimumReportByteInterval &&
                now - _lastReportTimestamp < MinimumReportTimeIntervalMilliseconds)
            {
                return;
            }

            _lastReportedBytes = ExtractedBytes;
            _lastReportTimestamp = now;
            progress.Report(new ModArchiveInstallProgress(stage, FileCount, ExtractedBytes, TotalBytes));
        }
    }
}
