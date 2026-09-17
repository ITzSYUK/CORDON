using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class AppPaths
{
    public static AppPaths Current { get; } = new();
    private readonly bool _preferGameDriveWorkspace;

    public AppPaths(string? executableDirectory = null)
        : this(executableDirectory, IsStandaloneExecutable(Environment.ProcessPath))
    {
    }

    internal AppPaths(string? executableDirectory, bool useLocalSettings, string? roamingConfigDirectory = null)
        : this(
            roamingConfigDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StalkerModLauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerModLauncher", "Workspaces"))
    {
        ExecutableDirectory = Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory);
        IsPortable = useLocalSettings;
        if (IsPortable)
        {
            ConfigDirectory = Path.Combine(ExecutableDirectory, "Data", "StalkerModLauncher");
            SettingsFile = Path.Combine(ConfigDirectory, "settings.json");
            SettingsBackupFile = Path.Combine(ConfigDirectory, "settings.backup.json");
        }
    }

    public AppPaths(string configDirectory, string workspaceRoot, bool preferGameDriveWorkspace = true)
    {
        ConfigDirectory = configDirectory;
        SettingsFile = Path.Combine(ConfigDirectory, "settings.json");
        SettingsBackupFile = Path.Combine(ConfigDirectory, "settings.backup.json");
        RoamingSettingsFile = SettingsFile;
        WorkspaceRoot = workspaceRoot;
        _preferGameDriveWorkspace = preferGameDriveWorkspace;
    }

    public string ConfigDirectory { get; }
    public string SettingsFile { get; }
    public string SettingsBackupFile { get; }
    internal string RoamingSettingsFile { get; }
    public string WorkspaceRoot { get; }
    public string ExecutableDirectory { get; } = AppContext.BaseDirectory;
    public bool IsPortable { get; }
    internal static bool IsStandaloneExecutable(string? path) =>
        string.Equals(Path.GetFileName(path), "CORDON-Standalone.exe", StringComparison.OrdinalIgnoreCase);
    public string CacheDirectory => IsPortable
        ? Path.Combine(ConfigDirectory, "Cache")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerModLauncher");
    public string TempDirectory => IsPortable
        ? Path.Combine(ConfigDirectory, "Temp")
        : Path.Combine(Path.GetTempPath(), "StalkerModLauncher");

    public void EnsureStorageWritable()
    {
        if (!IsPortable) return;
        foreach (var directory in new[] { ConfigDirectory, CacheDirectory, TempDirectory })
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
    }

    internal string ToStoredPath(string path)
    {
        if (!IsPortable || string.IsNullOrWhiteSpace(path)) return path;
        return FileSystemSafety.IsDirectoryInside(path, ExecutableDirectory)
            ? Path.GetRelativePath(ExecutableDirectory, path)
            : Path.GetFullPath(path);
    }

    internal void ValidateSourceDirectory(string source)
    {
        if (!IsPortable) return;
        if (new[] { ConfigDirectory, WorkspaceRoot, CacheDirectory, TempDirectory }.Any(root =>
                FileSystemSafety.IsDirectoryInside(source, root) ||
                FileSystemSafety.IsDirectoryInside(root, source)))
        {
            throw new InvalidOperationException(Strings.Portable_SourceOverlap);
        }
    }

    internal string FromStoredPath(string path)
    {
        if (!IsPortable || string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        var fullPath = Path.GetFullPath(path, ExecutableDirectory);
        if (!FileSystemSafety.IsDirectoryInside(fullPath, ExecutableDirectory))
            throw new InvalidDataException(Strings.Portable_PathEscapes);
        return fullPath;
    }

    public string GetPreferredWorkspaceRoot(string? gameInstallPath)
    {
        if (!_preferGameDriveWorkspace)
        {
            return WorkspaceRoot;
        }

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(gameInstallPath));
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return Path.Combine(root, "StalkerModLauncher", "Workspaces");
                }
            }
            catch
            {
                return WorkspaceRoot;
            }
        }

        return WorkspaceRoot;
    }

    public IReadOnlyList<string> GetManagedWorkspaceRoots(string? gameInstallPath)
    {
        return new[] { WorkspaceRoot, GetPreferredWorkspaceRoot(gameInstallPath) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string GetDefaultModInstallPath(string? gameInstallPath)
    {
        var workspaceRoot = GetPreferredWorkspaceRoot(gameInstallPath);
        var launcherRoot = Directory.GetParent(workspaceRoot)?.FullName ?? workspaceRoot;
        return Path.Combine(launcherRoot, "Mods");
    }

    public string GetLegacyProfileModInstallPath(string profileId, string? gameInstallPath)
    {
        var workspaceRoot = GetPreferredWorkspaceRoot(gameInstallPath);
        var launcherRoot = Directory.GetParent(workspaceRoot)?.FullName ?? workspaceRoot;
        var safeProfileId = FileSystemSafety.SanitizeName(profileId);
        return Path.Combine(launcherRoot, "Mods", $"profile-{safeProfileId}");
    }
}
