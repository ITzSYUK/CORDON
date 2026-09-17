using System.Globalization;
using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class SettingsStore : IDisposable
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

    public bool HasSettingsFile =>
        File.Exists(_paths.SettingsFile) || File.Exists(_paths.SettingsBackupFile);

    public event EventHandler<SettingsRecoveryInfo>? RecoveryCompleted;

    internal async Task<bool> TryImportRoamingSettingsAsync()
    {
        if (!_paths.IsPortable || HasSettingsFile || !File.Exists(_paths.RoamingSettingsFile))
        {
            return false;
        }

        await ImportAsync(_paths.RoamingSettingsFile);
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
        catch (SettingsPersistenceException ex)
        {
            _writeBlockReason = ex.Message;
            throw;
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
            var imported = await TryLoadFileAsync(sourcePath);
            if (imported.Settings is null)
            {
                if (imported.Kind == SettingsFileLoadKind.Unavailable)
                {
                    throw new SettingsPersistenceException(imported.Error ?? Strings.Settings_FileUnavailable);
                }

                throw new InvalidDataException(imported.Error ?? Strings.Settings_FileInvalid);
            }

            await SaveSnapshotCoreAsync(SerializeSettings(imported.Settings));
        }
        finally
        {
            _ioLock.Release();
        }
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
        catch (SettingsPersistenceException ex)
        {
            _writeBlockReason = ex.Message;
            throw;
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
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var primary = await TryLoadFileAsync(_paths.SettingsFile);
        if (primary.Settings is not null)
        {
            return new SettingsLoadResult(primary.Settings, null);
        }

        var backup = await TryLoadFileAsync(_paths.SettingsBackupFile);
        ThrowIfUnavailable(primary, backup);
        var damagedFiles = PreserveInvalidFiles(primary, backup);

        if (backup.Settings is not null)
        {
            await RestorePrimaryAsync(backup.Settings);
            return new SettingsLoadResult(
                backup.Settings,
                new SettingsRecoveryInfo(
                    SettingsRecoveryMode.Backup,
                    damagedFiles));
        }

        if (damagedFiles.Count == 0)
        {
            return new SettingsLoadResult(new AppSettings(), null);
        }

        var defaults = new AppSettings();
        await SaveSnapshotCoreAsync(JsonSerializer.SerializeToUtf8Bytes(defaults, JsonOptions));
        return new SettingsLoadResult(
            defaults,
            new SettingsRecoveryInfo(
                SettingsRecoveryMode.Defaults,
                damagedFiles));
    }

    private async Task<SettingsFileLoadResult> TryLoadFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsFileLoadResult(path, null, null);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions);
            if (settings is not null) settings = AppSettingsNormalizer.Normalize(settings);
            if (settings is not null && _paths.IsPortable)
            {
                TransformPaths(settings, _paths.FromStoredPath);
            }
            return settings is null
                ? new SettingsFileLoadResult(
                    path,
                    null,
                Strings.Settings_EmptyJson,
                    SettingsFileLoadKind.Corrupted)
                : new SettingsFileLoadResult(
                    path,
                    settings,
                    null,
                    SettingsFileLoadKind.Loaded);
        }
        catch (JsonException ex)
        {
            return new SettingsFileLoadResult(
                path,
                null,
                LocalizedText.Format(Strings.Settings_InvalidJsonFormat, ex.Message),
                SettingsFileLoadKind.Corrupted);
        }
        catch (IOException ex)
        {
            return new SettingsFileLoadResult(
                path,
                null,
                LocalizedText.Format(Strings.Settings_ReadErrorFormat, ex.Message),
                SettingsFileLoadKind.Unavailable);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SettingsFileLoadResult(
                path,
                null,
                LocalizedText.Format(Strings.Settings_AccessDeniedFormat, ex.Message),
                SettingsFileLoadKind.Unavailable);
        }
    }

    private List<RecoveredSettingsFile> PreserveInvalidFiles(
        params SettingsFileLoadResult[] results)
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

            foreach (var result in invalidFiles)
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

    private static void ThrowIfUnavailable(params SettingsFileLoadResult[] results)
    {
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

    private async Task RestorePrimaryAsync(AppSettings settings)
    {
        var snapshot = SerializeSettings(settings);
        var tempPath = _paths.SettingsFile + ".recovery.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, snapshot);
            File.Move(tempPath, _paths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
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
        if (!_paths.IsPortable) return JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        // Snapshot only: never replace the absolute paths observed by WPF with relative ones.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), JsonOptions)!;
        TransformPaths(snapshot, _paths.ToStoredPath);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    private static void TransformPaths(AppSettings settings, Func<string, string> transform)
    {
        settings.LastBrowsedGamePath = transform(settings.LastBrowsedGamePath ?? string.Empty);
        if (settings.LegacyGameInstallPath is not null) settings.LegacyGameInstallPath = transform(settings.LegacyGameInstallPath);
        foreach (var profile in settings.Profiles ?? [])
        {
            profile.GameInstallPath = transform(profile.GameInstallPath ?? string.Empty);
            profile.ExecutableSourcePath = transform(profile.ExecutableSourcePath ?? string.Empty);
            profile.FsgameSourcePath = transform(profile.FsgameSourcePath ?? string.Empty);
            profile.WorkspacePath = transform(profile.WorkspacePath ?? string.Empty);
            profile.ModInstallPath = transform(profile.ModInstallPath ?? string.Empty);
            profile.Mo2OverwritePath = transform(profile.Mo2OverwritePath ?? string.Empty);
            foreach (var mod in profile.Mods ?? []) mod.SourcePath = transform(mod.SourcePath ?? string.Empty);
        }
    }

    private async Task SaveSnapshotCoreAsync(byte[] snapshot)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var tempPath = _paths.SettingsFile + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, snapshot);

            if (File.Exists(_paths.SettingsFile))
            {
                File.Replace(tempPath, _paths.SettingsFile, _paths.SettingsBackupFile);
            }
            else
            {
                File.Move(tempPath, _paths.SettingsFile);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
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

    private sealed record SettingsFileLoadResult(
        string Path,
        AppSettings? Settings,
        string? Error,
        SettingsFileLoadKind Kind = SettingsFileLoadKind.Missing);

    private enum SettingsFileLoadKind
    {
        Missing,
        Loaded,
        Corrupted,
        Unavailable
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
