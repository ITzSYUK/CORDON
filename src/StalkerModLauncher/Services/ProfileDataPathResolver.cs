using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public static class ProfileDataPathResolver
{
    public static string GetGameDataRoot(ModProfile profile, string? workspacePath = null, string? gamePath = null)
    {
        var workspace = workspacePath ?? profile.WorkspacePath;
        if (!profile.UseBaseGameData || profile.IsStandalone)
        {
            return string.IsNullOrWhiteSpace(workspace) ? string.Empty : Path.Combine(workspace, "userdata");
        }

        var root = ProfileAppDataSourceLocator.ResolveConfiguredRoot(gamePath ?? profile.GameInstallPath);
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
        {
            if (!FileSystemSafety.IsFileSystemRoot(parent.FullName) &&
                File.Exists(Path.Combine(parent.FullName, WorkspaceBuilder.RootMarkerFileName)))
                throw new InvalidDataException($"Общий каталог данных {root} находится в управляемом хранилище профилей: {parent.FullName}.");
        }
        if (!string.IsNullOrWhiteSpace(workspace) &&
            (FileSystemSafety.IsDirectoryInside(root, workspace) || FileSystemSafety.IsDirectoryInside(workspace, root)))
        {
            throw new InvalidDataException("Общие данные игры не должны пересекаться с рабочей папкой профиля.");
        }
        var baseGamePath = gamePath ?? profile.GameInstallPath;
        if (!string.IsNullOrWhiteSpace(baseGamePath) && FileSystemSafety.IsDirectoryInside(baseGamePath, root))
        {
            throw new InvalidDataException("Каталог данных не должен совпадать с корнем игры либо содержать его.");
        }
        if (profile.Mods
            .Select(mod => mod.SourcePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => FileSystemSafety.IsDirectoryInside(path, root) ||
                         FileSystemSafety.IsDirectoryInside(root, path)))
        {
            throw new InvalidDataException("Каталог данных не должен пересекаться с исходной папкой мода.");
        }
        return root;
    }

    public static IReadOnlyList<string> GetLogDirectories(ModProfile profile)
    {
        return GetDataRoots(profile)
            .Select(root => Path.Combine(root, "logs"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetSavedGameDirectories(ModProfile profile)
    {
        return GetDataRoots(profile)
            .Select(root => Path.Combine(root, "savedgames"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetScreenshotDirectories(ModProfile profile)
    {
        return GetDataRoots(profile)
            .Select(root => Path.Combine(root, "screenshots"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetDataRoots(ModProfile profile)
    {
        if (!profile.IsStandalone)
        {
            try
            {
                var root = GetGameDataRoot(profile);
                return string.IsNullOrWhiteSpace(root) ? [] : [root];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                // Preflight reports the invalid configuration; do not open an unrelated fallback folder.
                return [];
            }
        }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath))
        {
            roots.Add(Path.Combine(profile.WorkspacePath, "userdata"));
        }

        foreach (var modRoot in profile.Mods
                     .Where(mod => mod.IsEnabled && Directory.Exists(mod.SourcePath))
                     .Select(mod => Path.GetFullPath(mod.SourcePath)))
        {
            var configuredRoot = TryResolveFsgameAppDataRoot(modRoot);
            if (configuredRoot is not null)
            {
                roots.Add(configuredRoot);
            }

            roots.Add(Path.Combine(modRoot, "appdata"));
            roots.Add(Path.Combine(modRoot, "userdata"));
            roots.Add(Path.Combine(modRoot, "_appdata_"));
            roots.Add(Path.Combine(modRoot, "bin", "_appdata_"));
            roots.Add(Path.Combine(modRoot, "bin_x64", "_appdata_"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryResolveFsgameAppDataRoot(string modRoot)
    {
        try
        {
            return ProfileAppDataSourceLocator.ResolveConfiguredRoot(modRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // Invalid fsgame.ltx should not prevent fallback path discovery.
            return null;
        }
    }
}
