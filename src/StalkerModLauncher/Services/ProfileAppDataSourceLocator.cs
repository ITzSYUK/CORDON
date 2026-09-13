using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

internal static class ProfileAppDataSourceLocator
{
    private static readonly string[] ConventionalRelativePaths =
    [
        "appdata",
        "userdata",
        "_appdata_",
        Path.Combine("bin", "_appdata_")
    ];

    public static IEnumerable<string> EnumerateRoots(FileLayer layer) =>
        EnumerateRoots(layer.RootPath);

    public static IEnumerable<string> EnumerateRoots(string layerRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredRoot = TryResolveConfiguredRoot(layerRoot);
        if (configuredRoot is not null && Directory.Exists(configuredRoot) && seen.Add(configuredRoot))
        {
            yield return configuredRoot;
        }

        foreach (var relativePath in ConventionalRelativePaths)
        {
            var candidate = Path.GetFullPath(Path.Combine(layerRoot, relativePath));
            if (Directory.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    internal static string ResolveConfiguredRoot(string layerRoot)
    {
        var configDirectory = ProfileDataConfigurator.FindFileDirectory(layerRoot, "fsgame.ltx")
            ?? throw new FileNotFoundException($"Не найден fsgame.ltx базовой игры: {layerRoot}");
        return ResolveConfiguredRootFromFile(Path.Combine(configDirectory, "fsgame.ltx"), layerRoot);
    }

    internal static string ResolveConfiguredRootFromFile(string configPath, string fsRoot)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(configPath, XRayTextEncoding.Config))
        {
            var content = line.Split(';', 2)[0];
            var assignment = content.IndexOf('=');
            if (assignment < 0) continue;
            aliases[content[..assignment].Trim()] = content[(assignment + 1)..]
                .Split('|', StringSplitOptions.TrimEntries);
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Resolve("$app_data_root$");

        string Resolve(string alias)
        {
            if (alias.Equals("$fs_root$", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(fsRoot);
            if (!visiting.Add(alias) || !aliases.TryGetValue(alias, out var parts) || parts.Length < 3)
            {
                throw new InvalidDataException($"Не удалось определить каталог данных базовой игры: неизвестный или циклический alias {alias}.");
            }

            var root = parts[2].Trim('"');
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException($"Пустой путь {alias} в fsgame.ltx.");
            var path = root.StartsWith('$') ? Resolve(root) : Path.GetFullPath(root, fsRoot);
            if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])) path = Path.GetFullPath(parts[3].Trim('"'), path);
            visiting.Remove(alias);
            return path;
        }
    }

    private static string? TryResolveConfiguredRoot(string layerRoot)
    {
        try
        {
            return ResolveConfiguredRoot(layerRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }
}
