using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class AppSettingsNormalizer
{
    public static AppSettings Normalize(AppSettings settings)
    {
        var schemaVersion = settings.SchemaVersion;
        settings.LastBrowsedGamePath ??= string.Empty;
        settings.DiscordClientId ??= string.Empty;
        if (string.IsNullOrWhiteSpace(settings.LastBrowsedGamePath) &&
            !string.IsNullOrWhiteSpace(settings.LegacyGameInstallPath))
        {
            settings.LastBrowsedGamePath = settings.LegacyGameInstallPath;
        }

        settings.LegacyGameInstallPath = null;
        settings.UiLanguage = schemaVersion < 9
            ? UiLanguage.Russian
            : UiLanguage.Normalize(settings.UiLanguage);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        if (!Enum.IsDefined(settings.LogLevel))
        {
            settings.LogLevel = LauncherLogLevel.Standard;
        }

        if (!settings.ShowTrayIcon)
        {
            settings.StartMinimizedToTrayOnWindowsStartup = false;
            settings.MinimizeToTrayOnClose = false;
        }

        settings.Profiles ??= [];
        settings.Profiles = settings.Profiles.Where(profile => profile is not null).ToList();

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var profileIndex = 0; profileIndex < settings.Profiles.Count; profileIndex++)
        {
            var profile = settings.Profiles[profileIndex];
            profile.Id = EnsureUniqueId(profile.Id, profileIds);
            profile.Name = string.IsNullOrWhiteSpace(profile.Name)
                ? LocalizedText.Format(Strings.Profile_DefaultNameFormat, profileIndex + 1)
                : profile.Name.Trim();
            profile.Description ??= string.Empty;
            profile.LaunchArguments ??= string.Empty;
            profile.ExecutableRelativePath = string.IsNullOrWhiteSpace(profile.ExecutableRelativePath)
                ? @"bin\xr_3da.exe"
                : profile.ExecutableRelativePath;
            profile.ExecutableSourcePath ??= string.Empty;
            profile.FsgameSourcePath ??= string.Empty;
            profile.UsvfsExecutableOverrideRelativePath ??= string.Empty;
            if (!string.IsNullOrWhiteSpace(profile.UsvfsExecutableOverrideRelativePath) &&
                !AnomalyUsvfsEngineSelection.TryParseRelativePath(
                    profile.UsvfsExecutableOverrideRelativePath,
                    out _,
                    out _))
            {
                profile.UsvfsExecutableOverrideRelativePath = string.Empty;
            }
            profile.WorkspacePath ??= string.Empty;
            profile.WorkingDirectoryRelative ??= string.Empty;
            profile.GameInstallPath ??= string.Empty;
            profile.Mo2OverwritePath ??= string.Empty;
            if (!Enum.IsDefined(profile.LaunchBackendKind))
            {
                profile.LaunchBackendKind = LaunchBackendKind.LinkedWorkspace;
            }

            profile.IsRunning = false;
            profile.Mods ??= [];
            profile.Mods = new System.Collections.ObjectModel.ObservableCollection<ModEntry>(
                profile.Mods.Where(mod => mod is not null));

            var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = 1;
            foreach (var mod in profile.Mods)
            {
                mod.Id = EnsureUniqueId(mod.Id, modIds);
                mod.SourcePath ??= string.Empty;
                mod.GroupName ??= string.Empty;
                mod.ExcludedFiles ??= [];
                mod.Name = string.IsNullOrWhiteSpace(mod.Name)
                    ? GetFallbackModName(mod.SourcePath, order)
                    : mod.Name.Trim();
                mod.Order = order++;
                mod.HasOverlapsAbove = false;
            }
        }

        return settings;
    }

    private static string GetFallbackModName(string sourcePath, int order)
    {
        try
        {
            var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? LocalizedText.Format(Strings.Mod_DefaultNameFormat, order) : name;
        }
        catch
        {
            return LocalizedText.Format(Strings.Mod_DefaultNameFormat, order);
        }
    }

    private static string EnsureUniqueId(string? id, HashSet<string> existing)
    {
        var candidate = id?.Trim() ?? string.Empty;
        if (candidate.Length > 0 && existing.Add(candidate))
        {
            return candidate;
        }

        do
        {
            candidate = Guid.NewGuid().ToString("N");
        }
        while (!existing.Add(candidate));

        return candidate;
    }
}
