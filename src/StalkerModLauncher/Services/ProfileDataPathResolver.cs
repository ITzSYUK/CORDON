using System.Text;
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
        if (new[] { gamePath ?? profile.GameInstallPath }.Concat(profile.Mods.Select(mod => mod.SourcePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => FileSystemSafety.IsDirectoryInside(path, root)))
        {
            throw new InvalidDataException("Каталог данных не должен совпадать с корнем игры или мода либо содержать их.");
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
            var fsgamePath = FindFsgame(modRoot);
            if (fsgamePath is null)
            {
                return null;
            }

            foreach (var line in File.ReadLines(fsgamePath, Encoding.Default))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("$app_data_root$", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = trimmed.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length < 3)
                {
                    return null;
                }

                var configuredPath = parts
                    .Skip(2)
                    .LastOrDefault(part => !string.IsNullOrWhiteSpace(part) && !part.StartsWith('$'));
                if (configuredPath is null)
                {
                    return null;
                }

                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fsgamePath)!, configuredPath));
            }
        }
        catch
        {
            // Invalid fsgame.ltx should not prevent fallback path discovery.
        }

        return null;
    }

    private static string? FindFsgame(string modRoot)
    {
        var rootFile = Path.Combine(modRoot, "fsgame.ltx");
        if (File.Exists(rootFile))
        {
            return rootFile;
        }

        return Directory.EnumerateDirectories(modRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(directory => Path.Combine(directory, "fsgame.ltx"))
            .FirstOrDefault(File.Exists);
    }
}
