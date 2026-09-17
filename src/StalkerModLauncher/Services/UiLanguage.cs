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

        if (!hasLocalSettings && paths.IsPortable &&
            File.Exists(paths.RoamingSettingsFile) &&
            TryReadPreference(paths.RoamingSettingsFile, out var roamingValue))
        {
            return roamingValue;
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
            var schemaVersion = root.TryGetProperty(nameof(Models.AppSettings.SchemaVersion), out var schemaElement) &&
                                schemaElement.TryGetInt32(out var parsedSchema)
                ? parsedSchema
                : 0;
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
