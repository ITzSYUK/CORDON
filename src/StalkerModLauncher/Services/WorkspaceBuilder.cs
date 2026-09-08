using System.IO;
using System.Diagnostics;
using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public interface IProfileWorkspaceManager
{
    string EnsureProfileWorkspace(ModProfile profile, string gamePath, IProgress<string>? progress = null);
    void DeleteProfileWorkspace(ModProfile profile, string gamePath);
    void ClearProfileWorkspaceCache(ModProfile profile, string gamePath);
}

public sealed class WorkspaceBuilder : IProfileWorkspaceManager
{
    private const string MarkerFileName = ".stalker-launcher-workspace";
    internal const string RootMarkerFileName = ".stalker-launcher-workspace-root";
    private const string ManifestFileName = "build-manifest.json";
    private const string WorkspaceFormatVersion = "strict-links-v3";
    private readonly AppPaths _paths;
    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public WorkspaceBuilder(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<WorkspaceBuildResult> BuildAsync(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        FileLayerPlan? fileLayerPlan = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Build(gamePath, profile, progress, fileLayerPlan, cancellationToken), cancellationToken);
    }

    private WorkspaceBuildResult Build(
        string gamePath,
        ModProfile profile,
        IProgress<string> progress,
        FileLayerPlan? providedFileLayerPlan,
        CancellationToken cancellationToken)
    {
        FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, "Launch executable");

        if (profile.IsStandalone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BuildStandalone(profile, progress, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            throw new InvalidOperationException("Для неавтономного профиля выберите папку игры.");
        }

        if (!Directory.Exists(gamePath))
        {
            throw new DirectoryNotFoundException($"Game folder was not found: {gamePath}");
        }

        var totalTimer = Stopwatch.StartNew();
        var workspaceRoot = EnsureProfileWorkspace(profile, gamePath, progress);
        var currentWorkspace = Path.Combine(workspaceRoot, "current");
        FileSystemSafety.EnsureDirectoryInside(currentWorkspace, workspaceRoot);
        var fileLayerPlan = providedFileLayerPlan ?? FileLayerPlan.CreateLinkedWorkspace(gamePath, profile, workspaceRoot);
        progress.Report("Проверка файлов игры и модов...");
        var scanTimer = Stopwatch.StartNew();
        var sourceSnapshot = WorkspaceSourceScanner.Capture(fileLayerPlan, cancellationToken);
        var buildFingerprint = WorkspaceSourceScanner.CreateBuildFingerprint(WorkspaceFormatVersion, profile, sourceSnapshot, fileLayerPlan);
        scanTimer.Stop();
        var cachedExecutable = WorkspaceManifestStore.TryGetCachedExecutable(
            workspaceRoot,
            currentWorkspace,
            profile,
            buildFingerprint,
            progress);
        if (cachedExecutable is not null)
        {
            ProfileWritableGameFileStore.EnsureWorkspaceDirectories(currentWorkspace);
            ProfileWritableGameFileStore.CaptureFromWorkspace(currentWorkspace, workspaceRoot, progress);
            ProfileWritableGameFileStore.RestoreToCachedWorkspace(currentWorkspace, workspaceRoot, progress);
            var cachedWorkingDirectoryRelative = ProfileDataConfigurator.Configure(
                gamePath,
                currentWorkspace,
                workspaceRoot,
                progress,
                fileLayerPlan,
                cancellationToken);
            progress.Report($"Проверка источников: {FormatElapsed(scanTimer.Elapsed)}. Пересборка не требуется.");
            return new WorkspaceBuildResult(
                currentWorkspace,
                cachedExecutable,
                workspaceRoot,
                profile.ExecutableRelativePath,
                cachedWorkingDirectoryRelative);
        }

        WorkspaceMaterializer.ValidateLinkSupport(sourceSnapshot, workspaceRoot, progress);

        progress.Report("Подготовка чистой рабочей среды профиля...");
        var cleanupTimer = Stopwatch.StartNew();
        ProfileWritableGameFileStore.CaptureFromWorkspace(currentWorkspace, workspaceRoot, progress);
        WorkspaceMaterializer.DeleteWorkspaceContents(currentWorkspace, workspaceRoot, () => sourceSnapshot, progress);
        Directory.CreateDirectory(currentWorkspace);
        cleanupTimer.Stop();

        var stats = new WorkspaceBuildStats();

        progress.Report("Подключение базовой игры к рабочей среде...");
        var baseGameTimer = Stopwatch.StartNew();
        WorkspaceMaterializer.MirrorBaseGame(sourceSnapshot.Game, currentWorkspace, progress, stats, cancellationToken);
        baseGameTimer.Stop();

        var modsTimer = Stopwatch.StartNew();
        foreach (var layer in fileLayerPlan.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = layer.Mod!;
            WorkspaceMaterializer.ApplyMod(currentWorkspace, mod, sourceSnapshot.Mods[mod.Id], progress, stats, cancellationToken);
        }
        modsTimer.Stop();

        var configurationTimer = Stopwatch.StartNew();
        var workingDirectoryRelative = ProfileDataConfigurator.Configure(
            gamePath,
            currentWorkspace,
            workspaceRoot,
            progress,
            fileLayerPlan,
            cancellationToken);
        ProfileWritableGameFileStore.RestoreToWorkspace(currentWorkspace, workspaceRoot, stats, progress);
        ApplyPinnedExecutableSource(profile, currentWorkspace, progress, stats);
        configurationTimer.Stop();

        var executablePath = Path.Combine(currentWorkspace, profile.ExecutableRelativePath);
        var executableRelativePath = profile.ExecutableRelativePath;
        if (!File.Exists(executablePath))
        {
            var detectedExecutable = WorkspaceExecutableResolver.Resolve(currentWorkspace, profile.ExecutableRelativePath, progress);
            if (detectedExecutable is null)
            {
                var discovered = Directory.EnumerateFiles(currentWorkspace, "*.exe", SafeEnumerationOptions)
                    .Select(path => Path.GetRelativePath(currentWorkspace, path))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray();
                var details = discovered.Length == 0
                    ? "No executable files were found in the built workspace."
                    : $"Executables found: {string.Join(", ", discovered)}";

                throw new FileNotFoundException(
                    $"Profile executable was not found after workspace build: {profile.ExecutableRelativePath}. {details}",
                    executablePath);
            }

            executablePath = detectedExecutable.FullPath;
            executableRelativePath = detectedExecutable.RelativePath;
        }

        progress.Report($"Рабочая среда готова. Жёсткие ссылки: {stats.LinkedFiles:N0}; символические ссылки: {stats.SymbolicLinkedFiles:N0}; локальные файлы: {stats.ProtectedCopies:N0}.");
        if (stats.ProtectedCopies > 0)
        {
            progress.Report($"Локальные файлы workspace: {stats.ProtectedCopies:N0}. Служебные файлы: {stats.RequiredLocalFiles:N0}; копии read-only: {stats.ReadOnlyCopiedFiles:N0}. Исходные файлы игры и модов не изменены.");
        }

        if (stats.ReadOnlyHandledFiles > 0)
        {
            progress.Report($"Файлы «только чтение»: {stats.ReadOnlyHandledFiles:N0}. Символические ссылки: {stats.ReadOnlySymbolicLinkedFiles:N0}; независимые копии: {stats.ReadOnlyCopiedFiles:N0}. Исходные файлы модов не изменены.");
        }

        totalTimer.Stop();
        progress.Report(
            $"Время подготовки: проверка {FormatElapsed(scanTimer.Elapsed)}; очистка {FormatElapsed(cleanupTimer.Elapsed)}; " +
            $"игра {FormatElapsed(baseGameTimer.Elapsed)}; моды {FormatElapsed(modsTimer.Elapsed)}; " +
            $"настройка {FormatElapsed(configurationTimer.Elapsed)}; всего {FormatElapsed(totalTimer.Elapsed)}.");
        WorkspaceManifestStore.Write(workspaceRoot, buildFingerprint, stats);
        return new WorkspaceBuildResult(
            currentWorkspace,
            executablePath,
            workspaceRoot,
            executableRelativePath,
            workingDirectoryRelative);
    }

    private static void ApplyPinnedExecutableSource(
        ModProfile profile,
        string currentWorkspace,
        IProgress<string> progress,
        WorkspaceBuildStats stats)
    {
        var sourceRoot = ProfileExecutableSourceResolver.FindPinnedSourceRoot(profile);
        if (sourceRoot is null)
        {
            if (!string.IsNullOrWhiteSpace(profile.ExecutableSourcePath))
            {
                throw new InvalidOperationException(
                    "Ручной источник бинарника больше недоступен. Выберите файл запуска заново или сбросьте источник на автоматический.");
            }

            return;
        }

        var sourceFile = FileSystemSafety.ResolvePathInside(
            sourceRoot.RootPath,
            profile.ExecutableRelativePath,
            "Pinned launch executable");
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException(
                $"Ручной источник бинарника найден, но файл отсутствует: {sourceFile}",
                sourceFile);
        }

        WorkspaceMaterializer.ReplaceFile(sourceFile, currentWorkspace, profile.ExecutableRelativePath, stats);
        progress.Report($"Используется вручную выбранный бинарник: {profile.ExecutableRelativePath}. Источник: {sourceRoot.DisplayName}.");
    }

    public static string GetSavedGamesPath(ModProfile profile)
    {
        var directories = ProfileDataPathResolver.GetSavedGameDirectories(profile);
        return directories.Count == 0 ? string.Empty : directories[0];
    }

    public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
    {
        var workspacePaths = FindGeneratedWorkspacePaths(profile, gamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath) && Directory.Exists(profile.WorkspacePath))
        {
            workspacePaths.Add(Path.GetFullPath(profile.WorkspacePath));
        }

        foreach (var workspacePath in workspacePaths)
        {
            DeleteProfileWorkspaceAtPath(profile, gamePath, workspacePath);
        }
    }

    internal void DeleteProfileWorkspaceAtPath(
        ModProfile profile,
        string gamePath,
        string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
        {
            return;
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var allowedRoot = FindAllowedWorkspaceParent(fullWorkspacePath, gamePath);
        if (allowedRoot is null)
        {
            throw new InvalidOperationException($"Refusing to delete workspace outside managed launcher roots: {fullWorkspacePath}");
        }

        var markerPath = Path.Combine(fullWorkspacePath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            RestoreGeneratedWorkspaceMarker(profile, fullWorkspacePath, allowedRoot);
        }

        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException($"Refusing to delete profile workspace without launcher marker file: {fullWorkspacePath}");
        }

        WorkspaceMaterializer.DeleteWorkspaceContents(
            fullWorkspacePath,
            allowedRoot,
            () => WorkspaceSourceScanner.Capture(gamePath, profile, CancellationToken.None));
    }

    public void ClearProfileWorkspaceCache(ModProfile profile, string gamePath)
    {
        ClearProfileWorkspaceCache(profile, gamePath, progress: null);
    }

    public void ClearProfileWorkspaceCache(
        ModProfile profile,
        string gamePath,
        IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) || !Directory.Exists(profile.WorkspacePath))
        {
            return;
        }

        var workspacePath = Path.GetFullPath(profile.WorkspacePath);
        var allowedRoot = FindAllowedWorkspaceParent(workspacePath, gamePath);
        if (allowedRoot is null)
        {
            throw new InvalidOperationException("Лаунчер отказался очищать папку без защитного маркера workspace.");
        }

        if (RestoreGeneratedWorkspaceMarker(profile, workspacePath, allowedRoot))
        {
            progress?.Report("Восстановлен защитный маркер профиля. Рабочую папку снова можно безопасно очищать.");
        }
        if (!File.Exists(Path.Combine(workspacePath, MarkerFileName)))
        {
            throw new InvalidOperationException("Лаунчер отказался очищать папку без защитного маркера workspace.");
        }

        var current = Path.Combine(workspacePath, "current");
        if (Directory.Exists(current))
        {
            ProfileWritableGameFileStore.CaptureFromWorkspace(current, workspacePath, progress);
            WorkspaceMaterializer.DeleteWorkspaceContents(
                current,
                workspacePath,
                () => WorkspaceSourceScanner.Capture(gamePath, profile, CancellationToken.None));
        }

        var manifest = Path.Combine(workspacePath, ManifestFileName);
        if (File.Exists(manifest))
        {
            File.Delete(manifest);
        }
    }

    public string EnsureProfileWorkspace(
        ModProfile profile,
        string gamePath,
        IProgress<string>? progress = null)
    {
        var preferredRoot = _paths.GetPreferredWorkspaceRoot(gamePath);
        var managedRoots = _paths.GetManagedWorkspaceRoots(gamePath);

        var existingGeneratedWorkspace = FindGeneratedWorkspacePaths(profile, gamePath)
            .OrderByDescending(path =>
                !string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
                FileSystemSafety.IsSameDirectory(path, profile.WorkspacePath))
            .ThenByDescending(path => File.Exists(Path.Combine(path, MarkerFileName)))
            .ThenByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (existingGeneratedWorkspace is not null)
        {
            existingGeneratedWorkspace = MigrateLegacyGeneratedWorkspace(
                profile,
                existingGeneratedWorkspace,
                progress);
        }

        var generatedWorkspacePath = existingGeneratedWorkspace ??
                                     Path.Combine(preferredRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        var workspacePath = string.IsNullOrWhiteSpace(profile.WorkspacePath) ||
                            ShouldRefreshUnusedGeneratedPath(profile, managedRoots)
            ? generatedWorkspacePath
            : profile.WorkspacePath;

        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) && existingGeneratedWorkspace is not null)
        {
            progress?.Report($"Найдена существующая рабочая папка профиля: {existingGeneratedWorkspace}");
        }

        var hasCustomWorkspaceMarker = File.Exists(Path.Combine(workspacePath, MarkerFileName)) &&
                                       HasManagedParentMarker(workspacePath);
        if (!hasCustomWorkspaceMarker &&
            !string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
            !string.IsNullOrWhiteSpace(gamePath) &&
            !FileSystemSafety.IsSameDirectory(Path.GetPathRoot(workspacePath)!, Path.GetPathRoot(preferredRoot)!))
        {
            workspacePath = Path.Combine(preferredRoot, ProfileManager.CreateWorkspaceDirectoryName(profile));
        }

        if (!hasCustomWorkspaceMarker && !managedRoots.Any(root =>
                FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                !FileSystemSafety.IsSameDirectory(workspacePath, root)))
        {
            throw new InvalidOperationException($"Profile workspace must be a profile-specific folder inside a managed launcher workspace root: {workspacePath}");
        }

        var workspaceAlreadyExisted = Directory.Exists(workspacePath);
        var workspaceManagedRoot = managedRoots.FirstOrDefault(root =>
                                       FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                                       !FileSystemSafety.IsSameDirectory(workspacePath, root)) ??
                                   Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName ??
                                   preferredRoot;
        var workspaceRootAlreadyExisted = Directory.Exists(workspaceManagedRoot);
        Directory.CreateDirectory(workspacePath);
        if (EnsureWorkspaceRootMarker(workspaceManagedRoot) && workspaceRootAlreadyExisted)
        {
            progress?.Report("Восстановлен служебный маркер корня workspace.");
        }

        var markerPath = Path.Combine(workspacePath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            WriteWorkspaceMarker(markerPath);
            if (workspaceAlreadyExisted)
            {
                progress?.Report("Восстановлен защитный маркер профиля. Рабочую папку снова можно безопасно пересобирать.");
            }
        }

        return workspacePath;
    }

    private string? FindAllowedWorkspaceParent(string workspacePath, string gamePath)
    {
        var managedRoot = _paths.GetManagedWorkspaceRoots(gamePath)
            .Select(Path.GetFullPath)
            .FirstOrDefault(root =>
                FileSystemSafety.IsDirectoryInside(workspacePath, root) &&
                !FileSystemSafety.IsSameDirectory(workspacePath, root));
        if (managedRoot is not null)
        {
            return managedRoot;
        }

        if (!File.Exists(Path.Combine(workspacePath, MarkerFileName)) || !HasManagedParentMarker(workspacePath))
        {
            return null;
        }

        return Directory.GetParent(workspacePath)?.FullName;
    }

    private static bool HasManagedParentMarker(string workspacePath)
    {
        var parent = Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName;
        return parent is not null && !FileSystemSafety.IsFileSystemRoot(parent) &&
               File.Exists(Path.Combine(parent, RootMarkerFileName));
    }

    private static bool RestoreGeneratedWorkspaceMarker(
        ModProfile profile,
        string workspacePath,
        string managedRoot)
    {
        if (!IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath))
        {
            return false;
        }

        Directory.CreateDirectory(managedRoot);
        var rootMarkerRestored = EnsureWorkspaceRootMarker(managedRoot);
        Directory.CreateDirectory(workspacePath);
        var markerPath = Path.Combine(workspacePath, MarkerFileName);
        var profileMarkerRestored = false;
        if (!File.Exists(markerPath))
        {
            WriteWorkspaceMarker(markerPath);
            profileMarkerRestored = true;
        }

        return rootMarkerRestored || profileMarkerRestored;
    }

    private static bool IsGeneratedWorkspaceOwnedByProfile(ModProfile profile, string workspacePath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath));
        var shortId = profile.Id.Length > 8 ? profile.Id[..8] : profile.Id;
        return directoryName.Equals(
                   ProfileManager.CreateWorkspaceDirectoryName(profile),
                   StringComparison.OrdinalIgnoreCase) ||
               directoryName.EndsWith($"-{shortId}", StringComparison.OrdinalIgnoreCase) ||
               directoryName.EndsWith($"-{profile.Id}", StringComparison.OrdinalIgnoreCase);
    }

    private static string MigrateLegacyGeneratedWorkspace(
        ModProfile profile,
        string workspacePath,
        IProgress<string>? progress)
    {
        var destinationName = ProfileManager.CreateWorkspaceDirectoryName(profile);
        var currentName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath));
        if (currentName.Equals(destinationName, StringComparison.OrdinalIgnoreCase))
        {
            return workspacePath;
        }

        var parent = Directory.GetParent(Path.GetFullPath(workspacePath))?.FullName ??
                     throw new IOException($"Workspace has no parent directory: {workspacePath}");
        var destination = Path.Combine(parent, destinationName);
        if (Directory.Exists(destination))
        {
            throw new IOException(
                $"Cannot migrate the legacy workspace because the destination already exists: {destination}");
        }

        LegacyProfileDataAlias.Delete(workspacePath, profile.Id);
        Directory.Move(workspacePath, destination);
        progress?.Report($"Workspace перенесён в совместимый ASCII-путь: {destination}");
        return destination;
    }

    private List<string> FindGeneratedWorkspacePaths(ModProfile profile, string gamePath)
    {
        var matches = new List<string>();
        foreach (var managedRoot in _paths.GetManagedWorkspaceRoots(gamePath)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(managedRoot))
            {
                continue;
            }

            try
            {
                matches.AddRange(Directory.EnumerateDirectories(managedRoot, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .Where(path => IsGeneratedWorkspaceOwnedByProfile(profile, path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An inaccessible optional workspace root must not hide valid roots.
            }
        }

        return matches;
    }

    private static bool EnsureWorkspaceRootMarker(string workspaceRoot)
    {
        if (FileSystemSafety.IsFileSystemRoot(workspaceRoot))
            throw new InvalidOperationException("Корень диска нельзя использовать как корень workspace.");
        Directory.CreateDirectory(workspaceRoot);
        var rootMarker = Path.Combine(workspaceRoot, RootMarkerFileName);
        if (!File.Exists(rootMarker))
        {
            File.WriteAllText(rootMarker, "Managed workspace root created by CORDON.");
            return true;
        }

        return false;
    }

    private static void WriteWorkspaceMarker(string markerPath)
    {
        File.WriteAllText(markerPath, "Managed by CORDON. It is safe for the launcher to recreate the 'current' subfolder.");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:0} мс"
            : $"{elapsed.TotalSeconds:0.0} с";
    }

    private static bool ShouldRefreshUnusedGeneratedPath(ModProfile profile, IReadOnlyList<string> managedRoots)
    {
        if (string.IsNullOrWhiteSpace(profile.WorkspacePath) || Directory.Exists(profile.WorkspacePath))
        {
            return false;
        }

        var workspacePath = Path.GetFullPath(profile.WorkspacePath);
        if (!managedRoots.Any(root => FileSystemSafety.IsDirectoryInside(workspacePath, root)))
        {
            return false;
        }

        return IsGeneratedWorkspaceOwnedByProfile(profile, workspacePath);
    }

    private static WorkspaceBuildResult BuildStandalone(
        ModProfile profile,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var modRoot = profile.Mods.FirstOrDefault(m => m.IsEnabled && Directory.Exists(m.SourcePath))?.SourcePath;
        if (modRoot is null)
        {
            throw new InvalidOperationException(
                "Standalone profile has no enabled mod with a valid folder. Add a mod and enable it.");
        }

        modRoot = Path.GetFullPath(modRoot);
        var exePath = FileSystemSafety.ResolvePathInside(modRoot, profile.ExecutableRelativePath, "Launch executable");
        var executableRelativePath = profile.ExecutableRelativePath;

        if (!File.Exists(exePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = WorkspaceExecutableResolver.ResolveStandalone(modRoot, profile.ExecutableRelativePath, cancellationToken);

            if (found is null)
            {
                throw new FileNotFoundException(
                    $"No executable found in standalone mod folder: {modRoot}", exePath);
            }

            executableRelativePath = found.RelativePath;
            exePath = found.FullPath;
            progress.Report($"Бинарник профиля не найден. Автоматически выбран '{found.RelativePath}': {found.Reason}.");
        }

        var workingDirectoryRelative = profile.WorkingDirectoryRelative;
        var fsgameDir = ProfileDataConfigurator.FindFileDirectory(modRoot, "fsgame.ltx");
        if (fsgameDir is not null)
        {
            var relativeDir = Path.GetRelativePath(modRoot, fsgameDir);
            if (relativeDir != ".")
            {
                workingDirectoryRelative = relativeDir;
            }
        }

        progress.Report($"Standalone mod ready: {profile.Name}");
        return new WorkspaceBuildResult(
            modRoot,
            exePath,
            profile.WorkspacePath,
            executableRelativePath,
            workingDirectoryRelative);
    }
}

public sealed record WorkspaceBuildResult(
    string WorkspaceRoot,
    string ExecutablePath,
    string ProfileWorkspacePath,
    string ExecutableRelativePath,
    string WorkingDirectoryRelative);
