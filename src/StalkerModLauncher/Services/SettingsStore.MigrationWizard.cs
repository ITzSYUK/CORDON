using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public sealed partial class SettingsStore
{
    internal string StorageDirectory => _paths.ConfigDirectory;

    internal async Task TransferDataAsync(IProgress<(double Fraction, string Path)> progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioLock.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                progress.Report((0, _paths.ConfigDirectory));
                using var worker = new SettingsStore(_paths);
                await worker.RunMigrationWizardAsync();
                progress.Report((1, _paths.ConfigDirectory));
            });
            _writeBlockReason = null;
        }
        finally { _ioLock.Release(); }
    }

    private async Task<AppSettings> RunMigrationWizardAsync()
    {
        var previous = _paths.MigrationDecision;
        try
        {
            var imported = await ReadAppDataProfilesAsync();
            var local = Path.Combine(_paths.ExecutableDirectory, "Data", "StalkerModLauncher");
            var localProfiles = await ReadProfilesFromDirectoryAsync(local);
            _paths.SetMigrationDecision(false);
            var settings = await LoadAsync();
            var ids = settings.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            settings.Profiles.AddRange(imported.Where(profile => ids.Add(profile.Id)));
            settings.Profiles.AddRange(localProfiles.Where(profile => ids.Add(profile.Id)));
            await SaveAsync(settings);
            return settings;
        }
        catch
        {
            if (previous is { } accepted) _paths.SetMigrationDecision(accepted);
            else _paths.ClearMigrationDecision();
            throw;
        }
    }
}
