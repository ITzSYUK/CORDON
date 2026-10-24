using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore
{
    private const string DeletedProfileIdsKey = "DeletedProfileIds";
    private const string UnavailableProfileIdsKey = "UnavailableProfileIds";

    private async Task<SettingsFileLoadResult<AppSettings>> TryLoadSettingsFileAsync(string path)
    {
        var file = await TryLoadFileAsync<JsonObject>(path);
        if (file.Settings is null) return new(path, null, file.Error, file.Kind);
        try
        {
            if (file.Settings.Count == 0)
                return new(path, null, Strings.Settings_FileInvalid, SettingsFileLoadKind.Corrupted);
            var settings = file.Settings.Deserialize<AppSettings>(JsonOptions)!;
            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                return new(path, null, LocalizedText.Format(Strings.Settings_SchemaUnsupportedFormat,
                    path, settings.SchemaVersion, AppSettings.CurrentSchemaVersion), SettingsFileLoadKind.Unsupported);
            if (settings.ProfileIds is { } ids)
            {
                if (ids.Any(string.IsNullOrWhiteSpace) ||
                    ids.Select(_paths.GetProfileFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                    return new(path, null, Strings.Settings_ProfileIndexInvalid, SettingsFileLoadKind.Corrupted);
            }
            else if (settings.SchemaVersion >= 10 && file.Settings.ContainsKey(nameof(AppSettings.SchemaVersion)) &&
                     file.Settings[nameof(AppSettings.Profiles)] is not JsonArray)
            {
                return new(path, null, Strings.Settings_ProfileIndexInvalid, SettingsFileLoadKind.Corrupted);
            }
            var paths = GetImportPaths(path);
            if (settings.ProfileIds is null)
            {
                foreach (var profile in settings.Profiles ?? [])
                {
                    if (profile is not null) ValidateStoredProfile(profile, paths);
                }
            }
            if (paths.IsPortable) TransformPaths(settings, value => { paths.FromStoredPath(value); return value; });
            return new(path, settings, null, SettingsFileLoadKind.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or InvalidDataException or NotSupportedException)
        {
            return new(path, null, LocalizedText.Format(Strings.Settings_InvalidJsonFormat, ex.Message), SettingsFileLoadKind.Corrupted);
        }
    }

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
                var primary = await TryLoadProfileAsync(path, id, paths);
                var profile = primary.Settings;
                if (profile is null)
                {
                    var backup = await TryLoadProfileAsync(GetProfileBackupFile(path), id, paths);
                    ThrowIfUnavailable(primary, backup);
                    profile = backup.Settings;
                    if (profile is null)
                    {
                        var error = LocalizedText.Format(Strings.Settings_ProfileUnavailableFormat,
                            path, primary.Error ?? backup.Error ?? Strings.Settings_ProfileFilesMissing);
                        if (!restore) throw new SettingsPersistenceException(error);
                        var legacy = await TryLoadSettingsFileAsync(Path.Combine(paths.ConfigDirectory, "settings.legacy.json"));
                        var name = legacy.Settings?.Profiles?.FirstOrDefault(item => item is not null &&
                            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
                        profile = new ModProfile
                        {
                            Id = id,
                            Name = LocalizedText.Format(Strings.Profile_UnavailableNameFormat, string.IsNullOrWhiteSpace(name) ? id : name),
                            SettingsLoadError = error,
                            HasLaunchError = true,
                            LaunchErrorSummary = error
                        };
                    }
                    else
                    {
                        if (restore)
                        {
                            recovered.AddRange(PreserveInvalidFiles([primary, backup]));
                            await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions));
                        }
                        usedBackup = true;
                    }
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

    private static async Task<SettingsFileLoadResult<ModProfile>> TryLoadProfileAsync(string path, string id, AppPaths paths)
    {
        var result = await TryLoadFileAsync<ModProfile>(path);
        if (result.Settings is not null && !string.Equals(result.Settings.Id, id, StringComparison.OrdinalIgnoreCase))
            return result with { Settings = null, Error = Strings.Settings_ProfileIndexInvalid, Kind = SettingsFileLoadKind.Corrupted };
        if (result.Settings is not null)
        {
            try { ValidateStoredProfile(result.Settings, paths); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                return result with { Settings = null, Error = ex.Message, Kind = SettingsFileLoadKind.Corrupted };
            }
        }
        return result;
    }

    private static void ValidateStoredProfile(ModProfile profile, AppPaths paths)
    {
        if (!double.IsFinite(profile.TotalPlaytimeSeconds) || profile.TotalPlaytimeSeconds < 0)
            throw new InvalidDataException(Strings.Settings_ProfilePlaytimeInvalid);
        try { _ = TimeSpan.FromSeconds(profile.TotalPlaytimeSeconds); }
        catch (OverflowException ex) { throw new InvalidDataException(Strings.Settings_ProfilePlaytimeInvalid, ex); }
        if (paths.IsPortable) TransformProfilePaths(profile, value => { paths.FromStoredPath(value); return value; });
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

    private async Task<SettingsLoadResult> RecoverUnindexedProfilesAsync()
    {
        var settings = new AppSettings();
        var damaged = new List<RecoveredSettingsFile>();
        if (!Directory.Exists(_paths.ProfilesDirectory)) return new(settings, null);

        bool MatchesPath(ModProfile? profile, string path) => profile is not null &&
            !string.IsNullOrWhiteSpace(profile.Id) &&
            FileSystemSafety.IsSameDirectory(path, _paths.GetProfileFile(profile.Id));

        var ids = new List<string>();
        var files = Directory.EnumerateFiles(_paths.ProfilesDirectory, "profile-*.json")
            .Select(path => path.EndsWith(".backup.json", StringComparison.OrdinalIgnoreCase)
                ? path[..^".backup.json".Length] + ".json" : path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var primary = await TryLoadFileAsync<ModProfile>(path);
            var backup = new SettingsFileLoadResult<ModProfile>(GetProfileBackupFile(path), null, null);
            if (!MatchesPath(primary.Settings, path)) backup = await TryLoadFileAsync<ModProfile>(backup.Path);
            ThrowIfUnavailable(primary, backup);
            var profile = MatchesPath(primary.Settings, path) ? primary.Settings : backup.Settings;
            if (!MatchesPath(profile, path))
            {
                if (primary.Settings is not null)
                    primary = primary with { Kind = SettingsFileLoadKind.Corrupted, Error = Strings.Settings_ProfileIndexInvalid };
                if (backup.Settings is not null)
                    backup = backup with { Kind = SettingsFileLoadKind.Corrupted, Error = Strings.Settings_ProfileIndexInvalid };
                damaged.AddRange(PreserveInvalidFiles([primary, backup], removeOriginals: false));
                continue;
            }
            ids.Add(profile!.Id);
        }
        settings.ProfileIds = ids;
        var loaded = await LoadProfilesAsync(settings, _paths);
        damaged.AddRange(loaded.Recovery?.Files ?? []);
        return new(loaded.Settings, damaged.Count > 0 ? new SettingsRecoveryInfo(SettingsRecoveryMode.Defaults, damaged) : null);
    }

    private async Task SaveSnapshotCoreAsync(byte[] snapshot)
    {
        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);
            await RecoverPendingSaveAsync();
            var previous = await TryLoadSettingsFileAsync(_paths.SettingsFile);
            if (previous.Kind == SettingsFileLoadKind.Missing)
                previous = await TryLoadSettingsFileAsync(_paths.SettingsBackupFile);
            ThrowIfUnavailable(previous);
            if (previous.Kind == SettingsFileLoadKind.Corrupted)
                throw new SettingsPersistenceException(Strings.Settings_FileInvalid);
            var document = JsonNode.Parse(snapshot)!.AsObject();
            var settings = document.Deserialize<AppSettings>(JsonOptions)!;
            var activeFiles = settings.Profiles.Select(profile => _paths.GetProfileFile(profile.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousIds = previous.Settings?.ProfileIds ?? previous.Settings?.Profiles?
                .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.Id)).Select(profile => profile.Id).ToList() ?? [];
            var removedIds = previousIds.Where(id => !activeFiles.Contains(_paths.GetProfileFile(id))).ToArray();
            document[DeletedProfileIdsKey] = JsonSerializer.SerializeToNode(removedIds);
            ValidateSnapshot(settings, document);
            snapshot = Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions));
            // Persist the journal before replacing any profile or the index.
            await WriteAtomicAsync(_paths.SettingsPendingFile, snapshot);
            await ApplySnapshotCoreAsync(snapshot);
            File.Delete(_paths.SettingsPendingFile);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
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
        try
        {
            if (pending.Settings is null || pending.Settings[nameof(AppSettings.Profiles)] is not JsonArray profiles ||
                profiles.Any(profile => profile is not JsonObject item || item[nameof(ModProfile.Id)] is not JsonValue id ||
                    !id.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) ||
                pending.Settings[nameof(AppSettings.SchemaVersion)] is not JsonValue version ||
                !version.TryGetValue<int>(out var schemaVersion) || schemaVersion != AppSettings.CurrentSchemaVersion)
                throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid);
            var settings = pending.Settings.Deserialize<AppSettings>(JsonOptions)!;
            if (settings.ProfileIds is not null) throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid);
            ReadProfileIds(pending.Settings, DeletedProfileIdsKey);
            ReadProfileIds(pending.Settings, UnavailableProfileIdsKey);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid, ex);
        }

        await ApplySnapshotCoreAsync(Encoding.UTF8.GetBytes(pending.Settings!.ToJsonString(JsonOptions)), replay: true);
        File.Delete(_paths.SettingsPendingFile);
    }

    private async Task ApplySnapshotCoreAsync(byte[] snapshot, bool preserveSettingsBackup = false, bool replay = false)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(snapshot, JsonOptions)!;
        var document = JsonNode.Parse(snapshot)!.AsObject();
        ValidateSnapshot(settings, document);
        var deletedIds = ReadProfileIds(document, DeletedProfileIdsKey);
        var unavailableFiles = ReadProfileIds(document, UnavailableProfileIdsKey)
            .Select(_paths.GetProfileFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profileFiles = settings.Profiles.Select(profile => _paths.GetProfileFile(profile.Id)).ToArray();
        Directory.CreateDirectory(_paths.ProfilesDirectory);
        for (var index = 0; index < settings.Profiles.Count; index++)
        {
            var path = profileFiles[index];
            if (unavailableFiles.Contains(path)) continue;
            await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(settings.Profiles[index], JsonOptions),
                GetProfileBackupFile(path), skipUnchanged: true);
        }

        var indexSnapshot = SerializeIndex(settings, settings.Profiles.Select(profile => profile.Id));
        await WriteAtomicAsync(_paths.SettingsFile, indexSnapshot,
            preserveSettingsBackup ? null : _paths.SettingsBackupFile,
            skipUnchanged: replay);
        if (deletedIds.Length > 0)
        {
            // Both indexes must stop referencing deleted profiles before their files disappear.
            var backup = await TryLoadSettingsFileAsync(_paths.SettingsBackupFile);
            ThrowIfUnavailable(backup);
            if (backup.Settings is not null)
            {
                backup.Settings.Profiles = [];
                AppSettingsNormalizer.Normalize(backup.Settings);
                await WriteAtomicAsync(_paths.SettingsBackupFile,
                    SerializeIndex(backup.Settings, settings.Profiles.Select(profile => profile.Id)), skipUnchanged: true);
            }
            foreach (var id in deletedIds)
            {
                var path = _paths.GetProfileFile(id);
                FileSystemSafety.EnsureDirectoryInside(path, _paths.ProfilesDirectory);
                File.Delete(path);
                File.Delete(GetProfileBackupFile(path));
            }
        }
    }

    private void ValidateSnapshot(AppSettings settings, JsonObject document)
    {
        var activeFiles = settings.Profiles.Select(profile => _paths.GetProfileFile(profile.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailableFiles = ReadProfileIds(document, UnavailableProfileIdsKey)
            .Select(_paths.GetProfileFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (activeFiles.Count != settings.Profiles.Count || !unavailableFiles.IsSubsetOf(activeFiles) ||
            ReadProfileIds(document, DeletedProfileIdsKey).Any(id => activeFiles.Contains(_paths.GetProfileFile(id))))
            throw new SettingsPersistenceException(Strings.Settings_ProfileIndexInvalid);
        foreach (var profile in settings.Profiles)
        {
            if (!unavailableFiles.Contains(_paths.GetProfileFile(profile.Id))) ValidateStoredProfile(profile, _paths);
        }
    }

    private static string[] ReadProfileIds(JsonObject document, string key)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return [];
        if (node is not JsonArray ids || ids.Any(id => id is not JsonValue value ||
                !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
            throw new SettingsPersistenceException(Strings.Settings_PendingSaveInvalid);
        return ids.Select(id => id!.GetValue<string>()).ToArray();
    }

    private static byte[] SerializeIndex(AppSettings settings, IEnumerable<string> ids)
    {
        var document = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        document.Remove(nameof(AppSettings.Profiles));
        document[nameof(AppSettings.ProfileIds)] = JsonSerializer.SerializeToNode(ids);
        return Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions));
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
