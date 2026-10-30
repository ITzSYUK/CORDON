using System.Globalization;
using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private string? _writeBlockReason;
    private bool _disposed;

    public SettingsStore(AppPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool HasSettingsFile => AppPaths.HasSettingsFiles(_paths.ConfigDirectory);

    public event EventHandler<SettingsRecoveryInfo>? RecoveryCompleted;

    internal async Task<bool> TryImportRoamingSettingsAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioLock.WaitAsync();
        try { return await TryImportRoamingSettingsCoreAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _writeBlockReason = ex.Message;
            throw new SettingsPersistenceException(ex.Message, ex);
        }
        finally { _ioLock.Release(); }
    }

    private async Task<bool> TryImportRoamingSettingsCoreAsync()
    {
        if (!_paths.IsPortable) return TryMigrateLegacySettings();
        if (HasSettingsFile) return false;
        var source = _paths.SettingsImportFile;
        if (source is null || !AppPaths.HasSettingsFiles(Path.GetDirectoryName(source)!)) return false;
        if (!File.Exists(source)) source = Path.ChangeExtension(source, ".backup.json");

        await ImportCoreAsync(source);
        return true;
    }

    public async Task<AppSettings> LoadAsync()
    {
        var result = await LoadWithRecoveryAsync();
        if (result.Recovery is not null)
        {
            RecoveryCompleted?.Invoke(this, result.Recovery);
        }

        return result.Settings;
    }

    public async Task<SettingsLoadResult> LoadWithRecoveryAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioLock.WaitAsync();
        try
        {
            var result = await LoadCoreAsync();
            _writeBlockReason = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _writeBlockReason = ex.Message;
            throw new SettingsPersistenceException(ex.Message, ex);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = SerializeSettings(settings);
        await _ioLock.WaitAsync();
        try
        {
            EnsureWritesAllowed();
            await SaveSnapshotCoreAsync(snapshot);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task ImportAsync(string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(Path.GetExtension(sourcePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Settings_JsonRequired);
        }

        await _ioLock.WaitAsync();
        try
        {
            EnsureWritesAllowed();
            await ImportCoreAsync(sourcePath);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task ImportCoreAsync(string sourcePath)
    {
        var sourcePaths = GetImportPaths(sourcePath);
        if (File.Exists(sourcePaths.SettingsPendingFile))
            throw new SettingsPersistenceException(Strings.Settings_ImportPending);
        var imported = await TryLoadSettingsFileAsync(sourcePath);
        if (imported.Settings is null)
        {
            if (imported.Kind is SettingsFileLoadKind.Unavailable or SettingsFileLoadKind.Unsupported)
                throw new SettingsPersistenceException(imported.Error ?? Strings.Settings_FileUnavailable);
            throw new InvalidDataException(imported.Error ?? Strings.Settings_FileInvalid);
        }

        var loaded = await LoadProfilesAsync(imported.Settings, sourcePaths, restore: false);
        await SaveSnapshotCoreAsync(SerializeSettings(loaded.Settings));
    }

    internal async Task<IReadOnlyList<ModProfile>> ReadLegacyProfilesAsync()
    {
        var directory = _paths.LegacyConfigDirectory;
        if (directory is null || !AppPaths.HasSettingsFiles(directory)) return [];
        var sourcePaths = new AppPaths(directory, _paths.WorkspaceRoot);
        if (File.Exists(sourcePaths.SettingsPendingFile))
            throw new SettingsPersistenceException(Strings.Settings_ImportPending);
        var source = File.Exists(sourcePaths.SettingsFile) ? sourcePaths.SettingsFile : sourcePaths.SettingsBackupFile;
        var imported = await TryLoadSettingsFileAsync(source);
        if (imported.Settings is null)
            throw new SettingsPersistenceException(imported.Error ?? Strings.Settings_FileInvalid);
        if (imported.Settings.ProfileIds is null)
        {
            var profiles = imported.Settings.Profiles ?? [];
            if (profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)) ||
                profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
                throw new SettingsPersistenceException(Strings.Settings_ProfileIndexInvalid);
        }
        var loaded = await LoadProfilesAsync(imported.Settings, sourcePaths, restore: false);
        return loaded.Settings.Profiles;
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SettingsRecoveryInfo? recovery = null;
        AppSettings updated;
        await _ioLock.WaitAsync();
        try
        {
            EnsureWritesAllowed();
            var current = await LoadCoreAsync();
            recovery = current.Recovery;
            updated = update(current.Settings);
            await SaveSnapshotCoreAsync(SerializeSettings(updated));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _writeBlockReason = ex.Message;
            throw new SettingsPersistenceException(ex.Message, ex);
        }
        finally
        {
            _ioLock.Release();
        }

        if (recovery is not null)
        {
            RecoveryCompleted?.Invoke(this, recovery);
        }

        return updated;
    }

    private async Task<SettingsLoadResult> LoadCoreAsync()
    {
        await TryImportRoamingSettingsCoreAsync();
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await RecoverPendingSaveAsync();
        var primary = await TryLoadSettingsFileAsync(_paths.SettingsFile);
        if (primary.Settings is not null)
        {
            var legacy = primary.Settings.ProfileIds is null;
            var loaded = await LoadProfilesAsync(primary.Settings, _paths);
            if (legacy)
            {
                PreserveLegacySettings(_paths.SettingsFile);
                await SaveSnapshotCoreAsync(SerializeSettings(loaded.Settings));
            }
            return loaded;
        }

        var backup = await TryLoadSettingsFileAsync(_paths.SettingsBackupFile);
        ThrowIfUnavailable(primary, backup);
        if (backup.Settings is not null)
        {
            var legacy = backup.Settings.ProfileIds is null;
            var loaded = await LoadProfilesAsync(backup.Settings, _paths);
            if (legacy) PreserveLegacySettings(_paths.SettingsBackupFile);
            var damagedFiles = PreserveInvalidFiles([primary, backup]);
            await ApplySnapshotCoreAsync(SerializeSettings(loaded.Settings), preserveSettingsBackup: true);
            return new SettingsLoadResult(loaded.Settings, new SettingsRecoveryInfo(
                SettingsRecoveryMode.Backup, damagedFiles.Concat(loaded.Recovery?.Files ?? []).ToArray()));
        }

        var recoveredProfiles = await RecoverUnindexedProfilesAsync();
        var defaults = recoveredProfiles.Settings;
        var damaged = PreserveInvalidFiles([primary, backup]);
        damaged.AddRange(recoveredProfiles.Recovery?.Files ?? []);
        if (damaged.Count == 0 && defaults.Profiles.Count == 0)
            return new SettingsLoadResult(defaults, null);

        await SaveSnapshotCoreAsync(SerializeSettings(defaults));
        return new SettingsLoadResult(defaults, new SettingsRecoveryInfo(SettingsRecoveryMode.Defaults, damaged));
    }

    private static async Task<SettingsFileLoadResult<T>> TryLoadFileAsync<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return new SettingsFileLoadResult<T>(path, null, null);

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
            return value is null
                ? new SettingsFileLoadResult<T>(path, null, Strings.Settings_EmptyJson, SettingsFileLoadKind.Corrupted)
                : new SettingsFileLoadResult<T>(path, value, null, SettingsFileLoadKind.Loaded);
        }
        catch (JsonException ex)
        {
            return new SettingsFileLoadResult<T>(path, null,
                LocalizedText.Format(Strings.Settings_InvalidJsonFormat, ex.Message), SettingsFileLoadKind.Corrupted);
        }
        catch (IOException ex)
        {
            return new SettingsFileLoadResult<T>(path, null,
                LocalizedText.Format(Strings.Settings_ReadErrorFormat, ex.Message), SettingsFileLoadKind.Unavailable);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SettingsFileLoadResult<T>(path, null,
                LocalizedText.Format(Strings.Settings_AccessDeniedFormat, ex.Message), SettingsFileLoadKind.Unavailable);
        }
    }

    private List<RecoveredSettingsFile> PreserveInvalidFiles<T>(
        SettingsFileLoadResult<T>[] results, bool removeOriginals = true) where T : class
    {
        var invalidFiles = results
            .Where(result => result.Kind == SettingsFileLoadKind.Corrupted)
            .ToArray();
        if (invalidFiles.Length == 0)
        {
            return [];
        }

        var recoveryDirectory = Path.Combine(_paths.ConfigDirectory, "recovery");
        Directory.CreateDirectory(recoveryDirectory);
        var timestamp = _timeProvider.GetLocalNow().ToString(
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture);
        var preserved = new List<RecoveredSettingsFile>(invalidFiles.Length);

        try
        {
            foreach (var result in invalidFiles)
            {
                var fileName = $"{Path.GetFileNameWithoutExtension(result.Path)}.corrupt-{timestamp}{Path.GetExtension(result.Path)}";
                var recoveryPath = GetUniquePath(recoveryDirectory, fileName);
                File.Copy(result.Path, recoveryPath);
                preserved.Add(new RecoveredSettingsFile(result.Path, recoveryPath, result.Error!));
            }

            foreach (var result in removeOriginals ? invalidFiles : [])
            {
                File.Delete(result.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsPersistenceException(
                    Strings.Settings_DamagedBackupFailed,
                ex);
        }

        return preserved;
    }

    private static void ThrowIfUnavailable<T>(params SettingsFileLoadResult<T>[] results) where T : class
    {
        var unsupported = results.FirstOrDefault(result => result.Kind == SettingsFileLoadKind.Unsupported);
        if (unsupported is not null) throw new SettingsPersistenceException(unsupported.Error!);
        var unavailable = results.FirstOrDefault(result => result.Kind == SettingsFileLoadKind.Unavailable);
        if (unavailable is null)
        {
            return;
        }

        throw new SettingsPersistenceException(
                LocalizedText.Format(Strings.Settings_TemporarilyUnavailableFormat, unavailable.Path, unavailable.Error));
    }

    private void EnsureWritesAllowed()
    {
        if (_writeBlockReason is not null)
        {
            throw new SettingsPersistenceException(
            LocalizedText.Format(Strings.Settings_WriteBlockedFormat, _writeBlockReason));
        }
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            path = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private byte[] SerializeSettings(AppSettings settings)
    {
        // Snapshot only: never mutate the IDs or absolute paths observed by WPF.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), JsonOptions)!;
        snapshot.ProfileIds = null;
        AppSettingsNormalizer.Normalize(snapshot);
        if (_paths.IsPortable) TransformPaths(snapshot, _paths.ToStoredPath);
        var unavailableIds = (settings.Profiles ?? []).Where(profile => profile is not null && profile.SettingsLoadError.Length > 0)
            .Select(profile => profile.Id).ToArray();
        if (unavailableIds.Length == 0) return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var document = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        document[UnavailableProfileIdsKey] = JsonSerializer.SerializeToNode(unavailableIds);
        return System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions));
    }

    private static void TransformPaths(AppSettings settings, Func<string, string> transform)
    {
        settings.LastBrowsedGamePath = transform(settings.LastBrowsedGamePath ?? string.Empty);
        settings.LastMo2SourcePath = transform(settings.LastMo2SourcePath ?? string.Empty);
        if (settings.LegacyGameInstallPath is not null) settings.LegacyGameInstallPath = transform(settings.LegacyGameInstallPath);
        foreach (var profile in settings.Profiles ?? [])
        {
            if (profile is not null) TransformProfilePaths(profile, transform);
        }
    }

    private static void TransformProfilePaths(ModProfile profile, Func<string, string> transform)
    {
        profile.GameInstallPath = transform(profile.GameInstallPath ?? string.Empty);
        profile.ExecutableSourcePath = transform(profile.ExecutableSourcePath ?? string.Empty);
        profile.FsgameSourcePath = transform(profile.FsgameSourcePath ?? string.Empty);
        profile.WorkspacePath = transform(profile.WorkspacePath ?? string.Empty);
        profile.ModInstallPath = transform(profile.ModInstallPath ?? string.Empty);
        profile.Mo2OverwritePath = transform(profile.Mo2OverwritePath ?? string.Empty);
        foreach (var mod in profile.Mods ?? [])
        {
            if (mod is not null) mod.SourcePath = transform(mod.SourcePath ?? string.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RecoveryCompleted = null;
        _ioLock.Dispose();
    }

    private sealed record SettingsFileLoadResult<T>(
        string Path,
        T? Settings,
        string? Error,
        SettingsFileLoadKind Kind = SettingsFileLoadKind.Missing);

    private enum SettingsFileLoadKind
    {
        Missing,
        Loaded,
        Corrupted,
        Unavailable,
        Unsupported
    }
}

public sealed class SettingsPersistenceException : IOException
{
    public SettingsPersistenceException(string message)
        : base(message)
    {
    }

    public SettingsPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum SettingsRecoveryMode
{
    Backup,
    Defaults
}

public sealed record RecoveredSettingsFile(
    string OriginalPath,
    string RecoveryPath,
    string Error);

public sealed record SettingsRecoveryInfo(
    SettingsRecoveryMode Mode,
    IReadOnlyList<RecoveredSettingsFile> Files);

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsRecoveryInfo? Recovery);
