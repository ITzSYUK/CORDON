using System.Diagnostics;
using System.Media;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    public void AddDroppedMods(IEnumerable<string> paths)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null)
        {
            return;
        }

        foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SelectedProfile.IsStandalone && SelectedProfile.Mods.Count >= 1)
            {
                break;
            }

            if (SelectedProfile.Mods.Any(mod => AreSamePaths(mod.SourcePath, path)))
            {
                continue;
            }

            SelectedMod = ModListEditor.Add(SelectedProfile, path);
        }

        RefreshValidation();
        _ = SaveAsync();
    }

    private static bool AreSamePaths(string left, string right)
    {
        try
        {
            return FileSystemSafety.IsSameDirectory(left, right);
        }
        catch
        {
            return false;
        }
    }

    public void MoveMod(ModEntry source, ModEntry target)
    {
        if (!CanEditSelectedProfile ||
            SelectedProfile is null ||
            !ModListEditor.Move(SelectedProfile, source, target))
        {
            return;
        }

        ModListEditor.SetGroup([source], target.GroupName);
        SelectedMod = source;
        CreateFilteredModsView();
        RaiseCommandStates();
    }

    public void MoveModToEnd(ModEntry source)
    {
        var targetGroup = SelectedProfile?.Mods.LastOrDefault(mod => !ReferenceEquals(mod, source))?.GroupName
                          ?? string.Empty;
        if (!CanEditSelectedProfile ||
            SelectedProfile is null ||
            !ModListEditor.MoveToEnd(SelectedProfile, source))
        {
            return;
        }

        ModListEditor.SetGroup([source], targetGroup);
        SelectedMod = source;
        CreateFilteredModsView();
        RaiseCommandStates();
    }

    public void MoveModToInsertionIndex(ModEntry source, int insertionIndex)
    {
        MoveModsToInsertionIndex([source], insertionIndex);
    }

    public void MoveModsToInsertionIndex(
        IReadOnlyList<ModEntry> sources,
        int insertionIndex,
        string? targetGroupName = null,
        bool preserveGroups = false)
    {
        if (!CanEditSelectedProfile ||
            SelectedProfile is null ||
            sources.Count == 0)
        {
            return;
        }

        var profile = SelectedProfile;
        var moved = false;
        var regrouped = false;
        if (!RunModReorder(profile, () =>
            {
                moved = ModListEditor.MoveManyToInsertionIndex(profile, sources, insertionIndex);
                regrouped = !preserveGroups && ModListEditor.SetGroup(sources, targetGroupName ?? string.Empty);
                return moved || regrouped;
            }))
        {
            return;
        }

        CompleteModReorder(profile, sources[^1], moved);
    }

    private bool RunModReorder(ModProfile profile, Func<bool> reorder)
    {
        _profilesReorderingMods.Add(profile);
        try
        {
            return reorder();
        }
        finally
        {
            _profilesReorderingMods.Remove(profile);
        }
    }

    private void CompleteModReorder(
        ModProfile profile,
        ModEntry selectedMod,
        bool affectsOverlay)
    {
        RefreshFilteredModsView();

        SelectedMod = selectedMod;
        _validationCache.Remove(profile);
        _automaticExecutableRefreshTimes[profile] = DateTime.UtcNow;
        RefreshAutomaticExecutableSelection(profile, Strings.Log_ReasonModListChanged);
        if (affectsOverlay)
        {
            RecalculateModOverlayInfo();
        }

        RefreshValidation();
        _autoSave.Schedule();
        RaiseCommandStates();
    }

    public void MoveModsToStart(IReadOnlyList<ModEntry> sources)
    {
        MoveModsToBoundary(sources, moveToEnd: false, preserveGroups: false);
    }

    public void MoveModsToEnd(IReadOnlyList<ModEntry> sources)
    {
        MoveModsToBoundary(sources, moveToEnd: true, preserveGroups: false);
    }

    public void MoveModsWithinGroupToBoundary(IReadOnlyList<ModEntry> sources, bool moveToEnd)
    {
        if (SelectedProfile is null || sources.Count == 0)
        {
            return;
        }

        var groupName = sources[0].GroupName;
        var groupMods = GetModGroupMods(groupName);
        if (groupName.Length == 0 || groupMods.Count == 0 ||
            sources.Any(mod => !mod.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var insertionIndex = SelectedProfile.Mods.IndexOf(moveToEnd ? groupMods[^1] : groupMods[0]);
        MoveModsToInsertionIndex(
            sources,
            moveToEnd ? insertionIndex + 1 : insertionIndex,
            preserveGroups: true);
    }

    public void MoveModGroupToStart(string groupName) =>
        MoveModsToBoundary(GetModGroupMods(groupName), moveToEnd: false, preserveGroups: true);

    public void MoveModGroupToEnd(string groupName) =>
        MoveModsToBoundary(GetModGroupMods(groupName), moveToEnd: true, preserveGroups: true);

    public void MoveModGroupByOffset(string groupName, int offset)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || offset is not (-1 or 1))
        {
            return;
        }

        var groups = GetModGroupNames();
        var sourceIndex = groups.ToList().FindIndex(name =>
            name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        var targetIndex = sourceIndex + offset;
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= groups.Count)
        {
            return;
        }

        var targetMods = GetModGroupMods(groups[targetIndex]);
        var insertionIndex = offset < 0
            ? SelectedProfile.Mods.IndexOf(targetMods[0])
            : SelectedProfile.Mods.IndexOf(targetMods[^1]) + 1;
        MoveModsToInsertionIndex(
            GetModGroupMods(groupName),
            insertionIndex,
            preserveGroups: true);
    }

    private void MoveModsToBoundary(IReadOnlyList<ModEntry> sources, bool moveToEnd, bool preserveGroups)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || sources.Count == 0)
        {
            return;
        }

        var profile = SelectedProfile;
        var selected = sources.ToHashSet();
        var targetGroup = moveToEnd
            ? profile.Mods.LastOrDefault(mod => !selected.Contains(mod))?.GroupName ?? string.Empty
            : string.Empty;
        var moved = false;
        var regrouped = false;
        if (!RunModReorder(profile, () =>
            {
                moved = moveToEnd
                    ? ModListEditor.MoveManyToEnd(profile, sources)
                    : ModListEditor.MoveManyToStart(profile, sources);
                regrouped = !preserveGroups && ModListEditor.SetGroup(sources, targetGroup);
                return moved || regrouped;
            }))
        {
            return;
        }

        CompleteModReorder(profile, sources[^1], moved);
    }

    public IReadOnlyList<string> GetModGroupNames() => SelectedProfile is null
        ? []
        : ModListEditor.GetGroupNames(SelectedProfile);

    public IReadOnlyList<ModEntry> GetModGroupMods(string groupName) => SelectedProfile?.Mods
        .Where(mod => mod.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase))
        .ToArray() ?? [];

    public void SetModGroupEnabled(string groupName, bool enabled)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        var profile = SelectedProfile;
        var mods = GetModGroupMods(groupName).Where(mod => mod.IsEnabled != enabled).ToArray();
        if (mods.Length == 0)
        {
            return;
        }

        _profilesTogglingMods.Add(profile);
        try
        {
            foreach (var mod in mods)
            {
                mod.IsEnabled = enabled;
            }
        }
        finally
        {
            _profilesTogglingMods.Remove(profile);
        }

        _validationCache.Remove(profile);
        SynchronizeProfileFileWatchers();
        _automaticExecutableRefreshTimes[profile] = DateTime.UtcNow;
        RefreshAutomaticExecutableSelection(profile, Strings.Log_ReasonModPriorityChanged);
        RecalculateModOverlayInfo();
        RefreshValidation();
        _autoSave.Schedule();
    }

    public bool CreateModGroup(IReadOnlyList<ModEntry> mods, string groupName)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null ||
            !ModListEditor.CreateGroup(SelectedProfile, mods, groupName))
        {
            return false;
        }

        CreateFilteredModsView();
        _autoSave.Schedule();
        return true;
    }

    public bool RenameModGroup(string oldName, string newName)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null ||
            !ModListEditor.RenameGroup(SelectedProfile, oldName, newName))
        {
            return false;
        }

        var collapsed = SelectedProfile.CollapsedModGroups;
        if (collapsed.RemoveAll(name => name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            collapsed.Add(newName.Trim());
            SelectedProfile.CollapsedModGroups = [.. collapsed];
        }

        CreateFilteredModsView();
        _autoSave.Schedule();
        return true;
    }

    public void DeleteModGroup(string groupName)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null ||
            !ModListEditor.DeleteGroup(SelectedProfile, groupName))
        {
            return;
        }

        SelectedProfile.CollapsedModGroups = SelectedProfile.CollapsedModGroups
            .Where(name => !name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        CreateFilteredModsView();
        _autoSave.Schedule();
    }

    public void MoveModsToGroup(IReadOnlyList<ModEntry> mods, string groupName)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || mods.Count == 0)
        {
            return;
        }

        var profile = SelectedProfile;
        if (!RunModReorder(profile, () => ModListEditor.MoveToGroup(profile, mods, groupName)))
        {
            return;
        }

        CompleteModReorder(profile, mods[^1], affectsOverlay: true);
    }

    public bool IsModGroupCollapsed(string groupName) => SelectedProfile?.CollapsedModGroups.Any(
        name => name.Equals(groupName, StringComparison.OrdinalIgnoreCase)) == true;

    public void SetModGroupCollapsed(string groupName, bool collapsed)
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        var groups = SelectedProfile.CollapsedModGroups
            .Where(name => !name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (collapsed)
        {
            groups.Add(groupName);
        }

        SelectedProfile.CollapsedModGroups = groups;
        RefreshFilteredModsView(refresh: true);
        _autoSave.Schedule();
    }

    private async Task ScanForModsAsync()
    {
        if (!CanEditSelectedProfile || SelectedProfile is null)
        {
            return;
        }

        var folder = DialogService.PickFolder(Strings.Mod_ScanPickFolder);
        if (folder is null)
        {
            return;
        }

        try
        {
            IsBuilding = true;
            BuildProgressText = Strings.Mod_Scanning;
            RaiseCommandStates();
            var discovered = await ModScannerService.ScanFolderAsync(folder);

            if (discovered.Count == 0)
            {
                Log(Strings.Log_NoModsFound);
                _dialogService.ShowError(Strings.Mod_NotFoundTitle, Strings.Mod_NotFound);
                return;
            }

            var selectableMods = discovered.Select(SelectableMod.FromDiscovered).ToList();
            IReadOnlyList<SelectableMod>? selected;

            if (IsPdaInterfaceEnabled && ModScanSelectionRequested is not null)
            {
                var request = new ModScanSelectionEventArgs(selectableMods);
                ModScanSelectionRequested.Invoke(this, request);
                selected = await request.Completion;
            }
            else
            {
                var window = new Views.ScanResultsWindow();
                foreach (var mod in selectableMods)
                {
                    window.Mods.Add(mod);
                }

                selected = window.ShowDialog() == true
                    ? window.GetSelectedMods()
                    : null;
            }

            if (selected is not null)
            {
                Log(LocalizedText.Format(Strings.Log_ScanResultsFormat, selectableMods.Count, selected.Count));
                if (selected.Count == 0)
                {
                    Log(Strings.Log_NoModsSelected);
                    return;
                }

                var existingPaths = new HashSet<string>(SelectedProfile.Mods.Select(m => m.SourcePath), StringComparer.OrdinalIgnoreCase);
                var added = 0;

                foreach (var mod in selected)
                {
                    if (existingPaths.Contains(mod.Path))
                    {
                        continue;
                    }

                    ModListEditor.Add(SelectedProfile, mod.Path, mod.Name);
                    existingPaths.Add(mod.Path);
                    added++;
                }

                RefreshValidation();
                _ = SaveAsync();
                Log(LocalizedText.Format(Strings.Log_ModsAddedFromScanFormat, added));
            }
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_ScanFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Mod_ScanFailed, ex.Message);
        }
        finally
        {
            IsBuilding = false;
            BuildProgressText = string.Empty;
            RaiseCommandStates();
        }
    }

    private void AddMod()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var selected = DialogService.PickFolder(Strings.Creation_PickMod);
        if (selected is null)
        {
            return;
        }

        SelectedMod = ModListEditor.Add(SelectedProfile, selected);
        RefreshValidation();
        Log(LocalizedText.Format(Strings.Log_ModAddedFormat, selected));
        _ = SaveAsync();
    }

    private async Task InstallModArchiveAsync()
    {
        if (SelectedProfile is not { } profile || !CanInstallModArchive())
        {
            return;
        }

        var archivePath = DialogService.PickFile(
            Strings.Mod_PickArchive,
            Strings.Mod_ArchiveFilter);
        if (archivePath is null)
        {
            return;
        }

        try
        {
            IsBuilding = true;
            IsInstallingModArchive = true;
            IsModArchiveInstallCompleted = false;
            IsModArchiveInstallProgressIndeterminate = true;
            ModArchiveInstallProgress = 0;
            var archiveFileName = Path.GetFileName(archivePath);
            ModArchiveInstallProgressText = LocalizedText.Format(Strings.Mod_PreparingFormat, archiveFileName);
            BuildProgressText = ModArchiveInstallProgressText;
            RaiseCommandStates();

            var installRoot = string.IsNullOrWhiteSpace(profile.ModInstallPath)
                ? _paths.GetDefaultModInstallPath(profile.GameInstallPath)
                : profile.ModInstallPath;
            profile.ModInstallPath = ValidateModInstallPath(profile, installRoot);

            var destination = ModArchiveInstaller.PlanInstall(archivePath, profile.ModInstallPath);
            if (destination.RequiresConfirmation && !await ConfirmModArchiveInstallDestinationAsync(destination))
            {
                Log(LocalizedText.Format(Strings.Log_ArchiveCancelledFormat, destination.PackagePath));
                return;
            }

            IsInstallingModArchive = true;
            IsModArchiveInstallProgressIndeterminate = true;

            var progressTimer = Stopwatch.StartNew();
            var progress = new Progress<ModArchiveInstallProgress>(value =>
                UpdateModArchiveInstallProgress(value, archiveFileName, progressTimer.Elapsed));
            var result = await ModArchiveInstaller.InstallAsync(
                archivePath,
                profile.ModInstallPath,
                destination.PackageDirectoryName,
                progress);
            var installedMod = ModListEditor.Add(profile, result.ModPath, result.ModName);

            if (ReferenceEquals(SelectedProfile, profile))
            {
                SelectedMod = installedMod;
                if (profile.IsStandalone)
                {
                    AutoDetectStandaloneExecutable();
                }

                RefreshValidation();
            }

            await SaveAsync();
            Log(LocalizedText.Format(Strings.Log_ArchiveInstalledFormat, archivePath, result.ModPath));

            var databaseNote = result.DatabaseArchivesRelocated
                ? Environment.NewLine + Strings.Mod_DatabaseNote
                : string.Empty;
            var details = LocalizedText.Format(Strings.Mod_FileCountFormat, result.FileCount, databaseNote);
            SystemSounds.Asterisk.Play();
            IsInstallingModArchive = false;
            InstalledModArchiveName = result.ModName;
            InstalledModArchivePath = result.ModPath;
            InstalledModArchiveDetails = details;
            IsModArchiveInstallCompleted = true;
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_ArchiveFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Mod_InstallFailed, ex.Message);
        }
        finally
        {
            IsBuilding = false;
            IsInstallingModArchive = false;
            IsModArchiveInstallProgressIndeterminate = false;
            IsModArchiveInstallDestinationConflict = false;
            _modArchiveInstallDestinationChoice = null;
            ModArchiveInstallProgress = 0;
            ModArchiveInstallProgressText = string.Empty;
            BuildProgressText = string.Empty;
            RaiseCommandStates();
        }
    }

    private void UpdateModArchiveInstallProgress(
        ModArchiveInstallProgress progress,
        string archiveFileName,
        TimeSpan elapsed)
    {
        switch (progress.Stage)
        {
            case ModArchiveInstallStage.Inspecting:
                IsModArchiveInstallProgressIndeterminate = true;
                ModArchiveInstallProgress = 0;
                ModArchiveInstallProgressText = LocalizedText.Format(Strings.Mod_AnalyzingFormat, archiveFileName);
                break;

            case ModArchiveInstallStage.Extracting when progress.TotalBytes is > 0:
                IsModArchiveInstallProgressIndeterminate = false;
                ModArchiveInstallProgress = Math.Clamp(
                    progress.ExtractedBytes * 100d / progress.TotalBytes.Value,
                    0,
                    100);
                var remainingText = FormatArchiveInstallRemainingTime(progress, elapsed);
                ModArchiveInstallProgressText = LocalizedText.Format(
                    Strings.Mod_ExtractingPercentFormat,
                    archiveFileName,
                    ModArchiveInstallProgress,
                    WorkspaceStatus.FormatSize(progress.ExtractedBytes),
                    WorkspaceStatus.FormatSize(progress.TotalBytes.Value),
                    remainingText);
                break;

            case ModArchiveInstallStage.Extracting:
                IsModArchiveInstallProgressIndeterminate = true;
                ModArchiveInstallProgressText = LocalizedText.Format(
                    Strings.Mod_ExtractingSizeFormat,
                    archiveFileName,
                    WorkspaceStatus.FormatSize(progress.ExtractedBytes));
                break;

            case ModArchiveInstallStage.Finalizing:
                IsModArchiveInstallProgressIndeterminate = false;
                ModArchiveInstallProgress = 100;
                ModArchiveInstallProgressText = LocalizedText.Format(Strings.Mod_FinalizingFormat, archiveFileName);
                break;
        }

        BuildProgressText = ModArchiveInstallProgressText;
    }

    private async Task<bool> ConfirmModArchiveInstallDestinationAsync(ModArchiveInstallDestination destination)
    {
        IsInstallingModArchive = false;
        IsModArchiveInstallProgressIndeterminate = false;
        ModArchiveInstallDestinationConflictText = LocalizedText.Format(Strings.Mod_FolderExistsFormat, destination.ModName);
        ModArchiveInstallDestinationConflictAction = destination.PackagePath;
        IsModArchiveInstallDestinationConflict = true;
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _modArchiveInstallDestinationChoice = choice;
        try
        {
            return await choice.Task;
        }
        finally
        {
            IsModArchiveInstallDestinationConflict = false;
            _modArchiveInstallDestinationChoice = null;
        }
    }

    private void ResolveModArchiveInstallDestinationChoice(bool continueInstallation)
    {
        _modArchiveInstallDestinationChoice?.TrySetResult(continueInstallation);
    }

    private static string FormatArchiveInstallRemainingTime(
        ModArchiveInstallProgress progress,
        TimeSpan elapsed)
    {
        if (progress.TotalBytes is not > 0 ||
            progress.ExtractedBytes <= 0 ||
            progress.ExtractedBytes >= progress.TotalBytes.Value ||
            elapsed.TotalSeconds < 0.8)
        {
            return Strings.Common_EstimatingTime;
        }

        var remainingSeconds = elapsed.TotalSeconds *
                               (progress.TotalBytes.Value - progress.ExtractedBytes) /
                               progress.ExtractedBytes;
        if (!double.IsFinite(remainingSeconds) || remainingSeconds < 0)
        {
            return Strings.Common_EstimatingTime;
        }

        var remaining = TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.FromDays(99).TotalSeconds));
        if (remaining.TotalHours >= 1)
        {
            return LocalizedText.Format(Strings.Common_RemainingHoursFormat, (int)remaining.TotalHours, remaining.Minutes);
        }

        if (remaining.TotalMinutes >= 1)
        {
            return LocalizedText.Format(Strings.Common_RemainingMinutesFormat, (int)remaining.TotalMinutes, remaining.Seconds);
        }

        return LocalizedText.Format(Strings.Common_RemainingSecondsFormat, Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)));
    }

    private bool CanInstallModArchive() => !IsBuilding && CanAddMod();

    private static string ValidateModInstallPath(ModProfile profile, string installRoot)
    {
        var fullInstallRoot = Path.GetFullPath(installRoot);
        if (!string.IsNullOrWhiteSpace(profile.GameInstallPath) &&
            FileSystemSafety.IsDirectoryInside(fullInstallRoot, profile.GameInstallPath))
        {
            throw new InvalidOperationException(Strings.Mod_FolderInsideGame);
        }

        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath) &&
            (FileSystemSafety.IsDirectoryInside(fullInstallRoot, profile.WorkspacePath) ||
             FileSystemSafety.IsDirectoryInside(profile.WorkspacePath, fullInstallRoot)))
        {
            throw new InvalidOperationException(Strings.Mod_FolderOverlapsWorkspace);
        }

        if (profile.Mods.Any(mod =>
                !string.IsNullOrWhiteSpace(mod.SourcePath) &&
                FileSystemSafety.IsDirectoryInside(fullInstallRoot, mod.SourcePath)))
        {
            throw new InvalidOperationException(Strings.Mod_FolderInsideOtherMod);
        }

        return fullInstallRoot;
    }

    private void BrowseExecutable()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var initialPath = Directory.Exists(SelectedMod?.SourcePath) ? SelectedMod.SourcePath
            : !string.IsNullOrWhiteSpace(SelectedProfile.GameInstallPath) ? SelectedProfile.GameInstallPath
            : _lastBrowsedGamePath;
        var selected = DialogService.PickExecutable(Strings.Dialog_SelectLaunchExecutable, initialPath);
        if (selected is null)
        {
            return;
        }

        var selection = TryGetExecutableSelection(selected);
        if (selection is null)
        {
            _dialogService.ShowError(
                Strings.Dialog_InvalidExecutable,
                Strings.Dialog_ExecutableOutsideSources);
            return;
        }

        SelectedProfile.ExecutableRelativePath = selection.RelativePath;
        SelectedProfile.ExecutableSourcePath = !SelectedProfile.IsStandalone && selection.PinsSource
            ? selection.SourceRootPath
            : string.Empty;
        Log(!SelectedProfile.IsStandalone && selection.PinsSource
            ? LocalizedText.Format(Strings.Log_ExecutableSelectedSourceFormat, selection.RelativePath, selection.SourceName)
            : LocalizedText.Format(Strings.Log_ExecutableSelectedFormat, selection.RelativePath));
        RefreshValidation();
        _ = SaveAsync();
    }

    private void AutoDetectStandaloneExecutable()
    {
        var modRoot = SelectedProfile?.Mods
            .FirstOrDefault(m => m.IsEnabled && Directory.Exists(m.SourcePath))
            ?.SourcePath;

        if (modRoot is null)
        {
            return;
        }

        var currentExe = SelectedProfile!.ExecutableRelativePath;
        if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(Path.Combine(modRoot, currentExe)))
        {
            return;
        }

        var found = LaunchExecutableDetector.DetectBest(
            [new LaunchExecutableSearchRoot(modRoot, Strings.Profile_Standalone, 1, IsBaseGameRoot: true)],
            currentExe);

        if (found is null)
        {
            return;
        }

        SelectedProfile.ExecutableRelativePath = found.RelativePath;
        SelectedProfile.ExecutableSourcePath = string.Empty;
        Log(LocalizedText.Format(Strings.Log_StandaloneExeDetectedFormat, found.RelativePath));
    }

    public void RemoveMods(IReadOnlyList<ModEntry> mods)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || mods.Count == 0)
        {
            return;
        }

        var removed = ModListEditor.Remove(SelectedProfile, mods);
        RefreshValidation();
        Log(LocalizedText.Format(Strings.Log_ModsRemovedFormat, removed));
        _ = SaveAsync();
    }

    private void RemoveMod()
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || SelectedMod is null)
        {
            return;
        }

        var removed = SelectedMod;
        ModListEditor.Remove(SelectedProfile, [removed]);
        RefreshValidation();
        Log(LocalizedText.Format(Strings.Log_ModRemovedFormat, removed.Name));
        _ = SaveAsync();
    }

    private void MoveSelectedMod(int direction)
    {
        if (!CanEditSelectedProfile || SelectedProfile is null || SelectedMod is null)
        {
            return;
        }

        var targetGroup = SelectedProfile.Mods[SelectedProfile.Mods.IndexOf(SelectedMod) + direction].GroupName;
        if (!ModListEditor.MoveByOffset(SelectedProfile, SelectedMod, direction))
        {
            return;
        }

        ModListEditor.SetGroup([SelectedMod], targetGroup);
        CreateFilteredModsView();
        RaiseCommandStates();
    }

    private bool CanMoveSelectedMod(int direction)
    {
        return CanEditSelectedProfile &&
               SelectedProfile is not null &&
               SelectedMod is not null &&
               ModListEditor.CanMoveByOffset(SelectedProfile, SelectedMod, direction);
    }

    private void RemoveInlineMod(ModEntry? mod)
    {
        if (mod is not null)
        {
            RemoveMods([mod]);
        }
    }

    private void MoveInlineMod(ModEntry? mod, int direction)
    {
        if (!CanMoveInlineMod(mod, direction) ||
            SelectedProfile is null ||
            mod is null)
        {
            return;
        }

        var targetGroup = SelectedProfile.Mods[SelectedProfile.Mods.IndexOf(mod) + direction].GroupName;
        if (!ModListEditor.MoveByOffset(SelectedProfile, mod, direction))
        {
            return;
        }

        ModListEditor.SetGroup([mod], targetGroup);
        SelectedMod = mod;
        CreateFilteredModsView();
        RaiseCommandStates();
    }

    private bool CanMoveInlineMod(ModEntry? mod, int direction)
    {
        return CanEditSelectedProfile &&
               SelectedProfile is not null &&
               mod is not null &&
               ModListEditor.CanMoveByOffset(SelectedProfile, mod, direction);
    }

    private void OpenInlineModFolder(ModEntry? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            DialogService.OpenFolder(mod.SourcePath);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_OpenModFolderFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
        }
    }

    private bool CanAddMod()
    {
        if (!CanEditSelectedProfile || SelectedProfile is null)
        {
            return false;
        }

        return !SelectedProfile.IsStandalone || SelectedProfile.Mods.Count < 1;
    }

    private void OpenSelectedModFolder()
    {
        if (SelectedMod is null)
        {
            return;
        }

        try
        {
            DialogService.OpenFolder(SelectedMod.SourcePath);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_OpenModFolderFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
        }
    }
}
