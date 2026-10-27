using System.Globalization;
using System.Text.Json;

namespace StalkerModLauncher.Services;

public static class UiLanguage
{
    public const string System = "system";
    public const string English = "en";
    public const string Russian = "ru";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        English => English,
        Russian => Russian,
        _ => System
    };

    internal static CultureInfo Resolve(string value, CultureInfo systemCulture) => Normalize(value) switch
    {
        English => CultureInfo.GetCultureInfo("en-US"),
        Russian => CultureInfo.GetCultureInfo("ru-RU"),
        _ => systemCulture
    };

    internal static string ReadPreference(AppPaths paths)
    {
        var staging = Path.Combine(paths.ConfigDirectory, "settings-migration");
        if (!paths.IsPortable && File.Exists(Path.Combine(staging, ".ready")))
            return ReadPreference(new AppPaths(staging, paths.WorkspaceRoot));
        try
        {
            using var store = new SettingsStore(paths);
            if (store.ReadPendingUiLanguage() is { } pendingLanguage) return pendingLanguage;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Invalid journals are reported and block writes during normal loading.
        }

        var hasLocalSettings = false;
        foreach (var path in new[] { paths.SettingsFile, paths.SettingsBackupFile })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            hasLocalSettings = true;
            if (TryReadPreference(path, out var value))
            {
                return value;
            }
        }

        try
        {
            if (!hasLocalSettings && !AppPaths.HasSettingsFiles(paths.ConfigDirectory) &&
                paths.SettingsImportFile is { } source)
            {
                return ReadPreference(new AppPaths(Path.GetDirectoryName(source)!, paths.WorkspaceRoot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Normal loading reports inaccessible configuration after startup localization.
        }

        return System;
    }

    internal static void ApplyAtStartup(AppPaths paths)
    {
        var culture = Resolve(ReadPreference(paths), CultureInfo.InstalledUICulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static bool TryReadPreference(string path, out string value)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            value = System;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return false;
            var schemaVersion = 0;
            if (root.TryGetProperty(nameof(Models.AppSettings.SchemaVersion), out var schemaElement) &&
                (schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt32(out schemaVersion)))
                return false;
            if (schemaVersion < 9)
            {
                value = Russian;
                return true;
            }

            value = root.TryGetProperty(nameof(Models.AppSettings.UiLanguage), out var languageElement) &&
                    languageElement.ValueKind == JsonValueKind.String
                ? Normalize(languageElement.GetString())
                : System;
            return true;
        }
        catch (JsonException)
        {
            value = System;
            return false;
        }
        catch (IOException)
        {
            value = System;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            value = System;
            return false;
        }
    }
}
