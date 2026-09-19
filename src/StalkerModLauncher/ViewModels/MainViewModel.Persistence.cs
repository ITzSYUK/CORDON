using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    private async Task LoadAsync()
    {
        try
        {
            try
            {
                if (await _settingsStore.TryImportRoamingSettingsAsync())
                {
                    Log(LocalizedText.Format(Strings.Log_SettingsImportedAutomaticallyFormat, _paths.RoamingSettingsFile));
                }
            }
            catch (Exception ex)
            {
                Log(LocalizedText.Format(Strings.Log_AutomaticSettingsImportFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
                _dialogService.ShowError(Strings.Settings_ImportAppDataFailed, ex.Message);
            }

            var loadResult = await _settingsStore.LoadWithRecoveryAsync();
            var settings = loadResult.Settings;
            _uiLanguage = settings.UiLanguage;
            _lastBrowsedGamePath = settings.LastBrowsedGamePath;
            _isPdaInterfaceEnabled = settings.IsPdaInterfaceEnabled;
            _useNewPdaInterface = settings.UseNewPdaInterface;
            _showTrayIcon = settings.ShowTrayIcon;
            _startWithWindows = settings.StartWithWindows;
            _startMinimizedToTrayOnWindowsStartup = settings.StartMinimizedToTrayOnWindowsStartup;
            _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
            _autoCheckForUpdates = settings.AutoCheckForUpdates;
            _showUpdateNotifications = settings.ShowUpdateNotifications;
            _logLevel = settings.LogLevel;
            _applicationLogService.Level = settings.LogLevel;
            OnPropertyChanged(nameof(IsPdaInterfaceEnabled));
            OnPropertyChanged(nameof(UseNewPdaInterface));
            OnPropertyChanged(nameof(ShowTrayIcon));
            OnPropertyChanged(nameof(StartWithWindows));
            OnPropertyChanged(nameof(StartMinimizedToTrayOnWindowsStartup));
            OnPropertyChanged(nameof(MinimizeToTrayOnClose));
            OnPropertyChanged(nameof(AutoCheckForUpdates));
            OnPropertyChanged(nameof(ShowUpdateNotifications));
            OnPropertyChanged(nameof(LogLevel));
            OnPropertyChanged(nameof(UiLanguage));
            OnPropertyChanged(nameof(GameInstallPath));
            ActivityLog.Load([], settings.IsLogVisible);

            if (_startWithWindows)
            {
                try
                {
                    _startupRegistrationService.Configure(true, _startMinimizedToTrayOnWindowsStartup);
                }
                catch (Exception ex)
                {
                    Log(LocalizedText.Format(Strings.Log_StartupRefreshFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.DiscordClientId))
            {
                _launchCoordinator.ConfigureDiscord(
                    settings.DiscordClientId,
                    message => Log(message, LauncherLogLevel.ErrorsOnly));
            }

            Profiles.Clear();
            foreach (var profile in settings.Profiles)
            {
                _profileManager.EnsureDefaults(profile);
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            RefreshValidation();
            Log(Strings.Log_SettingsLoaded);

            if (loadResult.Recovery is not null)
            {
                ReportSettingsRecovery(loadResult.Recovery);
            }
        }
        catch (SettingsPersistenceException ex)
        {
            Log(LocalizedText.Format(Strings.Log_SettingsLoadBlockedFormat, ex), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(
                Strings.Settings_Unavailable,
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                Strings.Settings_WriteBlocked);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_SettingsLoadFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
        }
    }

    private Task SaveAsync() => SaveCoreAsync(throwOnFailure: false);

    private Task SaveOrThrowAsync() => SaveCoreAsync(throwOnFailure: true);

    private async Task SaveCoreAsync(bool throwOnFailure)
    {
        _autoSave.Cancel();
        try
        {
            foreach (var profile in Profiles)
            {
                ModListEditor.Renumber(profile);
            }

            await _settingsStore.UpdateAsync(existing => new AppSettings
            {
                LastBrowsedGamePath = _lastBrowsedGamePath,
                Profiles = Profiles.ToList(),
                DontShowAboutOnStartup = existing.DontShowAboutOnStartup,
                IsLogVisible = ActivityLog.IsVisible,
                IsPdaInterfaceEnabled = IsPdaInterfaceEnabled,
                UseNewPdaInterface = UseNewPdaInterface,
                ShowTrayIcon = ShowTrayIcon,
                StartWithWindows = StartWithWindows,
                StartMinimizedToTrayOnWindowsStartup = StartMinimizedToTrayOnWindowsStartup,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                AutoCheckForUpdates = AutoCheckForUpdates,
                ShowUpdateNotifications = ShowUpdateNotifications,
                LogLevel = LogLevel,
                UiLanguage = UiLanguage,
                DiscordClientId = existing.DiscordClientId
            });
            Log(Strings.Log_SettingsSaved);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_SettingsSaveFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    public async Task SaveAboutPreferenceAsync(bool dontShowAgain)
    {
        await _settingsStore.UpdateAsync(settings =>
        {
            settings.DontShowAboutOnStartup = dontShowAgain;
            return settings;
        });
    }

    private void ProfilesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(CanImportSettings));
        ImportSettingsCommand.RaiseCanExecuteChanged();
        SynchronizeProfileSubscriptions();
    }

    private void SynchronizeProfileSubscriptions()
    {
        var currentProfiles = Profiles.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var profile in _trackedProfiles.Where(profile => !currentProfiles.Contains(profile)).ToArray())
        {
            UntrackProfile(profile);
        }

        foreach (var profile in Profiles)
        {
            if (_trackedProfiles.Add(profile))
            {
                profile.PropertyChanged += ProfileOnPropertyChanged;
                SynchronizeModSubscriptions(profile);
            }
        }

        SynchronizeProfileFileWatchers();
    }

    private void UntrackProfile(ModProfile profile)
    {
        profile.PropertyChanged -= ProfileOnPropertyChanged;
        if (_trackedModCollections.Remove(profile, out var collection))
        {
            collection.CollectionChanged -= ModsOnCollectionChanged;
            _modCollectionOwners.Remove(collection);
        }

        foreach (var mod in _modOwners.Where(pair => ReferenceEquals(pair.Value, profile)).Select(pair => pair.Key).ToArray())
        {
            mod.PropertyChanged -= ModOnPropertyChanged;
            _modOwners.Remove(mod);
        }

        _trackedProfiles.Remove(profile);
        _filteredModViews.Remove(profile);
        _validationCache.Remove(profile);
        _automaticExecutableRefreshTimes.Remove(profile);
    }

    private void SynchronizeModSubscriptions(ModProfile profile)
    {
        if (!_trackedModCollections.TryGetValue(profile, out var trackedCollection) ||
            !ReferenceEquals(trackedCollection, profile.Mods))
        {
            if (trackedCollection is not null)
            {
                trackedCollection.CollectionChanged -= ModsOnCollectionChanged;
                _modCollectionOwners.Remove(trackedCollection);
            }

            _trackedModCollections[profile] = profile.Mods;
            _modCollectionOwners[profile.Mods] = profile;
            profile.Mods.CollectionChanged += ModsOnCollectionChanged;
            _filteredModViews.Remove(profile);
        }

        var currentMods = profile.Mods.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var mod in _modOwners
                     .Where(pair => ReferenceEquals(pair.Value, profile) && !currentMods.Contains(pair.Key))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            mod.PropertyChanged -= ModOnPropertyChanged;
            _modOwners.Remove(mod);
        }

        foreach (var mod in profile.Mods)
        {
            if (_modOwners.TryAdd(mod, profile))
            {
                mod.PropertyChanged += ModOnPropertyChanged;
            }
        }
    }

    private void ModsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not ObservableCollection<ModEntry> collection ||
            !_modCollectionOwners.TryGetValue(collection, out var profile))
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Move && _profilesReorderingMods.Contains(profile))
        {
            return;
        }

        SynchronizeModSubscriptions(profile);
        SynchronizeProfileFileWatchers();
        _validationCache.Remove(profile);
        _profilesRenumberingMods.Add(profile);
        try
        {
            ModListEditor.Renumber(profile);
        }
        finally
        {
            _profilesRenumberingMods.Remove(profile);
        }
        ModListEditor.UpdateViewGroupKeys(profile);

        _automaticExecutableRefreshTimes[profile] = DateTime.UtcNow;
        RefreshAutomaticExecutableSelection(profile, Strings.Log_ReasonModListChanged);
        if (ReferenceEquals(profile, SelectedProfile))
        {
            CreateFilteredModsView();
            RecalculateModOverlayInfo();
            RefreshValidation();
        }

        _autoSave.Schedule();
    }

    private void ProfileOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ModProfile profile)
        {
            return;
        }

        if (e.PropertyName is nameof(ModProfile.HasLaunchError) or nameof(ModProfile.LaunchErrorSummary))
        {
            return;
        }

        if (e.PropertyName == nameof(ModProfile.Mods))
        {
            SynchronizeModSubscriptions(profile);
            SynchronizeProfileFileWatchers();
            _automaticExecutableRefreshTimes[profile] = DateTime.UtcNow;
            RefreshAutomaticExecutableSelection(profile, Strings.Log_ReasonModListReplaced);
            if (ReferenceEquals(profile, SelectedProfile))
            {
                CreateFilteredModsView();
                RecalculateModOverlayInfo();
            }
        }

        if (e.PropertyName is nameof(ModProfile.GameInstallPath)
            or nameof(ModProfile.IsStandalone)
            or nameof(ModProfile.LaunchArguments)
            or nameof(ModProfile.ExecutableSourcePath)
            or nameof(ModProfile.FsgameSourcePath)
            or nameof(ModProfile.Mo2OverwritePath))
        {
            _automaticExecutableRefreshTimes.Remove(profile);
            SynchronizeProfileFileWatchers();
        }

        if (e.PropertyName == nameof(ModProfile.IsRunning))
        {
            if (ReferenceEquals(profile, SelectedProfile))
            {
                OnPropertyChanged(nameof(CanEditSelectedProfile));
                RaiseCommandStates();
            }

            return;
        }

        _validationCache.Remove(profile);
        if (ReferenceEquals(profile, SelectedProfile))
        {
            RefreshValidation();
        }

        _autoSave.Schedule();
    }

    private void ModOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModEntry.HasOverlapsAbove)
            or nameof(ModEntry.ConflictKind)
            or nameof(ModEntry.OverwrittenFileCount)
            or nameof(ModEntry.OverwrittenModCount)
            or nameof(ModEntry.OverwrittenConfigurationCount)
            or nameof(ModEntry.OverwrittenBinaryCount)
            or nameof(ModEntry.OverwrittenByFileCount)
            or nameof(ModEntry.OverwrittenByModCount)
            or nameof(ModEntry.OverwrittenByBinaryCount)
            or nameof(ModEntry.ProvidesLaunchExecutable)
            or nameof(ModEntry.RelatedModIds)
            or nameof(ModEntry.IsConflictRelated)
            or nameof(ModEntry.OverlayDetails)
            or nameof(ModEntry.ConflictDisplay)
            or nameof(ModEntry.OverlaySummary)
            or nameof(ModEntry.HasOverlayInfo)
            or nameof(ModEntry.ViewGroupKey)
            or nameof(ModEntry.ShowsGroupHeader)
            or nameof(ModEntry.IsGroupCollapsed)
            or nameof(ModEntry.IsVisibleInModList))
        {
            return;
        }

        if (sender is not ModEntry changedMod || !_modOwners.TryGetValue(changedMod, out var profile))
        {
            return;
        }

        if (_profilesReorderingMods.Contains(profile) &&
            e.PropertyName is nameof(ModEntry.Order) or nameof(ModEntry.GroupName))
        {
            return;
        }

        if (e.PropertyName == nameof(ModEntry.GroupName))
        {
            if (ReferenceEquals(profile, SelectedProfile))
            {
                RefreshFilteredModsView(refresh: true);
            }
        }

        _validationCache.Remove(profile);
        if (_profilesRenumberingMods.Contains(profile))
        {
            return;
        }

        var affectsOverlay = e.PropertyName is nameof(ModEntry.IsEnabled)
            or nameof(ModEntry.Order)
            or nameof(ModEntry.SourcePath)
            or nameof(ModEntry.Name)
            or nameof(ModEntry.ExcludedFiles);
        if (affectsOverlay)
        {
            if (e.PropertyName is nameof(ModEntry.SourcePath)
                or nameof(ModEntry.IsEnabled)
                or nameof(ModEntry.ExcludedFiles))
            {
                SynchronizeProfileFileWatchers();
            }

            _automaticExecutableRefreshTimes[profile] = DateTime.UtcNow;
            RefreshAutomaticExecutableSelection(profile, Strings.Log_ReasonModPriorityChanged);
        }

        if (ReferenceEquals(profile, SelectedProfile))
        {
            if (affectsOverlay)
            {
                RecalculateModOverlayInfo();
            }

            RefreshValidation();
        }

        _autoSave.Schedule();
    }

    private void ReportSettingsRecovery(SettingsRecoveryInfo recovery)
    {
        foreach (var file in recovery.Files)
        {
            Log(
                LocalizedText.Format(Strings.Log_SettingsRecoveryFormat, file.OriginalPath, file.RecoveryPath, file.Error),
                LauncherLogLevel.ErrorsOnly);
        }

        var message = recovery.Mode == SettingsRecoveryMode.Backup
            ? recovery.Files.Count > 0
                ? Strings.Settings_PrimaryRecoveredDamaged
                : Strings.Settings_PrimaryRecoveredMissing
            : Strings.Settings_RecoveryFailed;

        Log(message, LauncherLogLevel.ErrorsOnly);
        var preservedFiles = recovery.Files.Count == 0
            ? string.Empty
            : LocalizedText.Format(
                Strings.Settings_DamagedFilesFormat,
                Environment.NewLine,
                string.Join(Environment.NewLine, recovery.Files.Select(file => file.RecoveryPath)));
        DialogService.ShowInfo(
            Strings.Settings_RecoveryTitle,
            message + preservedFiles);
    }

    private void SettingsStoreOnRecoveryCompleted(object? sender, SettingsRecoveryInfo recovery)
    {
        _ = InvokeOnUiAsync(() => ReportSettingsRecovery(recovery));
    }

    private void RefreshAutomaticExecutableSelection(
        ModProfile profile,
        string reason,
        bool preferExistingRelativePath = false)
    {
        if (profile.IsStandalone ||
            !string.IsNullOrWhiteSpace(profile.ExecutableSourcePath))
        {
            return;
        }

        var selection = preferExistingRelativePath
            ? ProfileExecutableSourceResolver.TryResolveExistingAutomaticSelection(profile)
            : null;
        selection ??= ProfileExecutableSourceResolver.DetectAutomaticSelection(
            profile,
            includeWorkspace: false);
        if (selection is null ||
            profile.ExecutableRelativePath.Equals(selection.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        profile.ExecutableRelativePath = selection.RelativePath;
        Log(
            LocalizedText.Format(Strings.Log_ExecutableDetectedAfterFormat, reason, selection.RelativePath, selection.SourceName),
            LauncherLogLevel.Detailed);
    }

    private void SynchronizeProfileFileWatchers()
    {
        foreach (var watcher in _profileFileWatchers)
        {
            watcher.Dispose();
        }

        _profileFileWatchers.Clear();
        _profileFsgamePaths = Profiles
            .SelectMany(GetProfileFsgamePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watchDirectories = Profiles
            .SelectMany(GetProfileFileTargets)
            .SelectMany(GetWatchDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .Aggregate(new List<string>(), (paths, path) =>
            {
                if (!paths.Any(parent =>
                        !FileSystemSafety.IsFileSystemRoot(parent) &&
                        FileSystemSafety.IsDirectoryInside(path, parent)))
                {
                    paths.Add(path);
                }

                return paths;
            });

        var requests = new List<(string Path, bool IncludeSubdirectories)>();
        foreach (var path in watchDirectories)
        {
            requests.Add((path, !FileSystemSafety.IsFileSystemRoot(path)));
            for (var ancestor = Directory.GetParent(path); ancestor is not null; ancestor = ancestor.Parent)
            {
                requests.Add((ancestor.FullName, false));
            }
        }

        var requestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests.Where(request =>
                     requestKeys.Add($"{request.IncludeSubdirectories}:{request.Path}")))
        {
            try
            {
                var watcher = new FileSystemWatcher(request.Path)
                {
                    IncludeSubdirectories = request.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size
                };
                watcher.Changed += ProfileFileChanged;
                watcher.Created += ProfileFilesChanged;
                watcher.Deleted += ProfileFilesChanged;
                watcher.Renamed += ProfileFilesChanged;
                watcher.Error += ProfileFilesChanged;
                watcher.EnableRaisingEvents = true;
                _profileFileWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Readiness still refreshes on profile selection when a location cannot be watched.
            }
        }
    }

    private static IEnumerable<string> GetProfileFileTargets(ModProfile profile)
    {
        yield return profile.GameInstallPath;
        yield return profile.Mo2OverwritePath;
        yield return profile.ExecutableSourcePath;
        yield return Path.GetDirectoryName(profile.FsgameSourcePath) ?? string.Empty;
        foreach (var mod in profile.Mods)
        {
            yield return mod.SourcePath;
        }
    }

    private static IReadOnlyList<string> GetProfileFsgamePaths(ModProfile profile)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(profile.FsgameSourcePath))
            {
                return [Path.GetFullPath(profile.FsgameSourcePath.Trim())];
            }

            var relativePath = FileLayerPlan.ResolveFsgameLaunchArgument(profile.LaunchArguments) ?? "fsgame.ltx";
            return new[] { profile.GameInstallPath }
                .Concat(profile.Mods.Where(mod => mod.IsEnabled).Select(mod => mod.SourcePath))
                .Append(profile.Mo2OverwritePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(Path.Combine(path, relativePath)))
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> GetWatchDirectories(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return [];
        }

        try
        {
            var fullPath = Path.GetFullPath(target.Trim());
            var parent = Directory.GetParent(fullPath);
            while (parent is not null && !Directory.Exists(parent.FullName))
            {
                parent = parent.Parent;
            }

            if (parent is not null)
            {
                return FileSystemSafety.IsFileSystemRoot(parent.FullName) && Directory.Exists(fullPath)
                    ? [parent.FullName, fullPath]
                    : [parent.FullName];
            }

            return Directory.Exists(fullPath) ? [fullPath] : [];
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void ProfileFilesChanged(object sender, FileSystemEventArgs e) => _profileFileRefresh.Schedule();

    private void ProfileFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_profileFsgamePaths.Contains(e.FullPath))
        {
            _profileFileRefresh.Schedule();
        }
    }

    private void ProfileFilesChanged(object sender, ErrorEventArgs e) => _profileFileRefresh.Schedule();

    private void RefreshProfileFileState()
    {
        if (_disposed)
        {
            return;
        }

        SynchronizeProfileFileWatchers();
        RefreshProfileLaunchReadiness(forceRefresh: true);
        _modConflictAnalyzer.ClearCache();
        RecalculateModOverlayInfo();
    }

    public async Task CleanupAsync()
    {
        await SaveAsync();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Profiles.CollectionChanged -= ProfilesOnCollectionChanged;
        _settingsStore.RecoveryCompleted -= SettingsStoreOnRecoveryCompleted;
        foreach (var profile in _trackedProfiles.ToArray())
        {
            UntrackProfile(profile);
        }

        foreach (var watcher in _profileFileWatchers)
        {
            watcher.Dispose();
        }

        _profileFileWatchers.Clear();

        if (_selectedProfile is not null)
        {
            _selectedProfile.PropertyChanged -= OnSelectedProfilePropertyChanged;
        }

        _autoSave.Dispose();
        _conflictAnalysisDebounce.Dispose();
        _profileFileRefresh.Dispose();
        _conflictAnalysisCancellation?.Cancel();
        _conflictAnalysisCancellation?.Dispose();
        _launchCoordinator.Dispose();
        ProfileCreationRequested = null;
        Mo2ImportRequested = null;
        ModScanSelectionRequested = null;
        ConflictExplorerRequested = null;
    }
}
