namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore
{
    private bool TryMigrateLegacySettings()
    {
        if (_paths.IsPortable || _paths.LegacyConfigDirectory is not { } source ||
            FileSystemSafety.IsSameDirectory(source, _paths.ConfigDirectory)) return false;

        var staging = Path.Combine(_paths.ConfigDirectory, "settings-migration");
        var ready = Path.Combine(staging, ".ready");
        if (!File.Exists(ready))
        {
            if (HasSettingsFile || !AppPaths.HasSettingsFiles(source)) return false;
            if (Directory.Exists(staging)) FileSystemSafety.DeleteDirectoryContents(staging, _paths.ConfigDirectory);
            Directory.CreateDirectory(staging);
            foreach (var name in new[] { "settings.json", "settings.backup.json", "settings.pending.json", "settings.legacy.json" })
            {
                var path = Path.Combine(source, name);
                if (File.Exists(path)) File.Copy(path, Path.Combine(staging, name));
            }
            foreach (var name in new[] { "profiles", "recovery" })
            {
                var directory = Path.Combine(source, name);
                if (!Directory.Exists(directory)) continue;
                Directory.CreateDirectory(Path.Combine(staging, name));
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                    File.Copy(path, Path.Combine(staging, name, Path.GetFileName(path)));
            }
            // A complete source copy must survive interruption while publishing multiple files.
            File.WriteAllText(ready, string.Empty);
        }

        foreach (var name in new[] { "profiles", "recovery" })
        {
            var directory = Path.Combine(staging, name);
            if (!Directory.Exists(directory)) continue;
            Directory.CreateDirectory(Path.Combine(_paths.ConfigDirectory, name));
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                File.Copy(path, Path.Combine(_paths.ConfigDirectory, name, Path.GetFileName(path)), overwrite: true);
        }
        foreach (var path in Directory.EnumerateFiles(staging, "*.json"))
            File.Copy(path, Path.Combine(_paths.ConfigDirectory, Path.GetFileName(path)), overwrite: true);

        FileSystemSafety.DeleteDirectoryContents(staging, _paths.ConfigDirectory);
        return true;
    }
}
