using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore
{
    private async Task<SettingsLoadResult> LoadProfilesAsync(AppSettings settings, AppPaths paths, bool restore = true)
    {
        var recovered = new List<RecoveredSettingsFile>();
        var usedBackup = false;
        if (settings.ProfileIds is { } ids)
        {
            if (ids.Any(string.IsNullOrWhiteSpace) ||
                ids.Select(paths.GetProfileFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                throw new SettingsPersistenceException(Strings.Settings_ProfileIndexInvalid);

            settings.Profiles = [];
            foreach (var id in ids)
            {
                var path = paths.GetProfileFile(id);
                var primary = await TryLoadProfileAsync(path, id);
                var profile = primary.Settings;
                if (profile is null)
                {
                    var backup = await TryLoadProfileAsync(GetProfileBackupFile(path), id);
                    ThrowIfUnavailable(primary, backup);
                    profile = backup.Settings;
                    if (profile is null)
                        throw new SettingsPersistenceException(LocalizedText.Format(
                            Strings.Settings_ProfileUnavailableFormat, path, primary.Error ?? backup.Error));

                    if (restore)
                    {
                        recovered.AddRange(PreserveInvalidFiles(primary, backup));
                        await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions));
                    }
                    usedBackup = true;
                }
                settings.Profiles.Add(profile);
            }
        }

        settings.ProfileIds = null;
        AppSettingsNormalizer.Normalize(settings);
        if (paths.IsPortable) TransformPaths(settings, paths.FromStoredPath);
        return new SettingsLoadResult(settings,
            usedBackup ? new SettingsRecoveryInfo(SettingsRecoveryMode.Backup, recovered) : null);
    }

    private static async Task<SettingsFileLoadResult<ModProfile>> TryLoadProfileAsync(string path, string id)
    {
        var result = await TryLoadFileAsync<ModProfile>(path);
        if (result.Settings is not null && !string.Equals(result.Settings.Id, id, StringComparison.OrdinalIgnoreCase))
            return result with { Settings = null, Error = Strings.Settings_ProfileIndexInvalid, Kind = SettingsFileLoadKind.Corrupted };
        return result;
    }

    private AppPaths GetImportPaths(string sourcePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
        if (FileSystemSafety.IsSameDirectory(directory.FullName, _paths.ConfigDirectory)) return _paths;
        return directory.Name.Equals("StalkerModLauncher", StringComparison.OrdinalIgnoreCase) &&
               directory.Parent?.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) == true
            ? new AppPaths(directory.Parent.Parent!.FullName, useLocalSettings: true)
            : new AppPaths(directory.FullName, _paths.WorkspaceRoot);
    }

    private void PreserveLegacySettings(string path)
    {
        var backup = Path.Combine(_paths.ConfigDirectory, "settings.legacy.json");
        if (!File.Exists(backup)) File.Copy(path, backup);
    }

    private async Task<AppSettings> RecoverUnindexedProfilesAsync()
    {
        var settings = new AppSettings();
        if (!Directory.Exists(_paths.ProfilesDirectory)) return settings;

        var ids = new List<string>();
        var files = Directory.EnumerateFiles(_paths.ProfilesDirectory, "profile-*.json")
            .Select(path => path.EndsWith(".backup.json", StringComparison.OrdinalIgnoreCase)
                ? path[..^".backup.json".Length] + ".json" : path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var file = await TryLoadFileAsync<ModProfile>(path);
            ThrowIfUnavailable(file);
            if (file.Settings is null)
            {
                var backup = await TryLoadFileAsync<ModProfile>(GetProfileBackupFile(path));
                ThrowIfUnavailable(backup);
                if (backup.Settings is not null) file = backup;
            }
            if (file.Settings is null)
                throw new SettingsPersistenceException(LocalizedText.Format(Strings.Settings_ProfileUnavailableFormat, path, file.Error));
            if (FileSystemSafety.IsSameDirectory(path, _paths.GetProfileFile(file.Settings.Id))) ids.Add(file.Settings.Id);
        }
        settings.ProfileIds = ids;
        return (await LoadProfilesAsync(settings, _paths)).Settings;
    }

    private async Task SaveSnapshotCoreAsync(byte[] snapshot)
    {
        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);
            await RecoverPendingSaveAsync();
            // Persist the journal before replacing any profile or the index.
            await WriteAtomicAsync(_paths.SettingsPendingFile, snapshot);
            await ApplySnapshotCoreAsync(snapshot);
            File.Delete(_paths.SettingsPendingFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writeBlockReason = ex.Message;
            throw new SettingsPersistenceException(ex.Message, ex);
        }
    }

    private async Task RecoverPendingSaveAsync()
    {
        var pending = await TryLoadFileAsync<JsonObject>(_paths.SettingsPendingFile);
        if (pending.Kind == SettingsFileLoadKind.Missing) return;
        ThrowIfUnavailable(pending);
        if (pending.Settings is null || pending.Settings[nameof(AppSettings.Profiles)] is not JsonArray ||
            pending.Settings[nameof(AppSettings.SchemaVersion)] is not JsonValue version ||
            !version.TryGetValue<int>(out var schemaVersion) || schemaVersion != AppSettings.CurrentSchemaVersion)
            throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid);

        AppSettings settings;
        try
        {
            settings = pending.Settings.Deserialize<AppSettings>(JsonOptions)!;
        }
        catch (JsonException ex)
        {
            throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid, ex);
        }
        if (settings.ProfileIds is not null ||
            settings.Profiles.Any(profile => profile is null || string.IsNullOrWhiteSpace(profile.Id)))
            throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid);

        await ApplySnapshotCoreAsync(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), replay: true);
        File.Delete(_paths.SettingsPendingFile);
    }

    private async Task ApplySnapshotCoreAsync(byte[] snapshot, bool preserveSettingsBackup = false, bool replay = false)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(snapshot, JsonOptions)!;
        var profileFiles = settings.Profiles.Select(profile => _paths.GetProfileFile(profile.Id)).ToArray();
        if (profileFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profileFiles.Length)
            throw new SettingsPersistenceException(Strings.Settings_ProfileIndexInvalid);

        Directory.CreateDirectory(_paths.ProfilesDirectory);
        for (var index = 0; index < settings.Profiles.Count; index++)
        {
            var path = profileFiles[index];
            await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(settings.Profiles[index], JsonOptions),
                GetProfileBackupFile(path), skipUnchanged: true);
        }

        var document = JsonNode.Parse(snapshot)!.AsObject();
        document.Remove(nameof(AppSettings.Profiles));
        document[nameof(AppSettings.ProfileIds)] = JsonSerializer.SerializeToNode(settings.Profiles.Select(profile => profile.Id));
        var indexSnapshot = Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions));
        await WriteAtomicAsync(_paths.SettingsFile, indexSnapshot,
            preserveSettingsBackup ? null : _paths.SettingsBackupFile,
            skipUnchanged: replay);
        // shortcut: unreferenced profile files are retained for recovery; prune only with an explicit retention policy.
    }

    private static string GetProfileBackupFile(string path) => Path.ChangeExtension(path, ".backup.json");

    private static async Task WriteAtomicAsync(string path, byte[] snapshot, string? backup = null, bool skipUnchanged = false)
    {
        if (skipUnchanged && File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path);
            if (snapshot.AsSpan().SequenceEqual(existing)) return;
        }
        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, snapshot);
            if (backup is not null && File.Exists(path)) File.Replace(tempPath, path, backup);
            else File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
