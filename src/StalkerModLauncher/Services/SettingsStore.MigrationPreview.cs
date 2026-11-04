namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore
{
    internal IReadOnlyList<string> GetProfileMigrationSources() => AppDataDirectories
        .Append(Path.Combine(_paths.ExecutableDirectory, "Data", "StalkerModLauncher"))
        .Where(AppPaths.HasSettingsFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

}
