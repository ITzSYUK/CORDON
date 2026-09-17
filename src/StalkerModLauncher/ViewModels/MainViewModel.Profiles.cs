using Microsoft.Win32;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    public void MoveProfileToInsertionIndex(ModProfile profile, int insertionIndex)
    {
        if (!ProfileManager.MoveToInsertionIndex(Profiles, profile, insertionIndex))
        {
            return;
        }

        SelectedProfile = profile;
        _autoSave.Schedule();
    }

    private void ExportProfile()
    {
        ExportProfile(SelectedProfile);
    }

    private void ExportProfile(ModProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Strings.Profile_ExportTitle,
            Filter = Strings.Dialog_ProfileFilter,
            FileName = $"{profile.Name}.stalkerprofile"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ProfileTransferService.Export(dialog.FileName, profile);
            Log(LocalizedText.Format(Strings.Log_ProfileExportedFormat, profile.Name));
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_ExportFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Profile_ExportError, ex.Message);
        }
    }

    private void ImportProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Profile_ImportTitle,
            Filter = Strings.Dialog_ProfileFilter,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var profile = ProfileTransferService.Import(dialog.FileName);
            _profileManager.PrepareImported(Profiles, profile);

            Profiles.Add(profile);
            SelectedProfile = profile;
            _ = SaveAsync();
            Log(LocalizedText.Format(Strings.Log_ProfileImportedFormat, profile.Name));
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_ImportFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Profile_ImportError, ex.Message);
        }
    }

    private async Task ImportSettingsAsync()
    {
        var path = DialogService.PickFile(
            Strings.Settings_ImportTitle,
            Strings.Common_JsonFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            _autoSave.Cancel();
            await _settingsStore.ImportAsync(path);
            await LoadAsync();
            Log(LocalizedText.Format(Strings.Log_SettingsImportedFormat, path));
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_SettingsImportFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Settings_ImportFailed, ex.Message);
        }
    }

    private void ChooseGameFolder()
    {
        var selected = DialogService.PickFolder(Strings.Dialog_SelectGogFolder, GameInstallPath);
        if (selected is null)
        {
            return;
        }

        _lastBrowsedGamePath = selected;
        GameInstallPath = selected;
        RefreshValidation();
        Log(LocalizedText.Format(Strings.Log_GameFolderSelectedFormat, selected));
    }

    private void NewProfile()
    {
        ProfileCreationRequested?.Invoke(this, EventArgs.Empty);
    }

    public void AddCreatedProfile(ModProfile profile)
    {
        _profileManager.EnsureDefaults(profile);
        profile.Name = ProfileManager.GetUniqueName(Profiles, profile.Name);
        Profiles.Add(profile);
        SelectedProfile = profile;
        Log(LocalizedText.Format(Strings.Log_ProfileCreatedFormat, profile.Name));
        _ = SaveAsync();
    }

    public Mo2ImportViewModel CreateMo2ImportViewModel() =>
        new(TryAddImportedProfileAsync);

    public async Task<bool> TryAddImportedProfileAsync(ModProfile profile)
    {
        var previousSelection = SelectedProfile;
        profile.Name = ProfileManager.GetUniqueName(Profiles, profile.Name);
        Profiles.Add(profile);
        SelectedProfile = profile;

        try
        {
            await SaveOrThrowAsync();
            Log(LocalizedText.Format(Strings.Log_Mo2ProfileImportedFormat, profile.Name));
            return true;
        }
        catch (Exception ex)
        {
            Profiles.Remove(profile);
            SelectedProfile = previousSelection is not null && Profiles.Contains(previousSelection)
                ? previousSelection
                : Profiles.FirstOrDefault();
            Log(LocalizedText.Format(Strings.Log_Mo2ImportRolledBackFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(
                Strings.Mo2_TransferFailed,
                LocalizedText.Format(Strings.Mo2_TransferCancelledFormat, Environment.NewLine, ex.Message));
            return false;
        }
    }

    private void DuplicateProfile()
    {
        DuplicateProfile(SelectedProfile);
    }

    private void DuplicateProfile(ModProfile? sourceProfile)
    {
        if (sourceProfile is null)
        {
            return;
        }

        var profile = _profileManager.Duplicate(Profiles, sourceProfile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        Log(LocalizedText.Format(Strings.Log_ProfileDuplicatedFormat, profile.Name));
        _ = SaveAsync();
    }

    private void DeleteProfile()
    {
        DeleteProfile(SelectedProfile);
    }

    private void DeleteProfile(ModProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        var deleteMessage = profile.IsStandalone
            ? LocalizedText.Format(Strings.Profile_DeleteStandaloneFormat, profile.Name)
            : LocalizedText.Format(Strings.Profile_DeleteRegularFormat, profile.Name);
        if (!DialogService.Confirm(Strings.Profile_DeleteTitle, deleteMessage))
        {
            return;
        }

        try
        {
            SelectedProfile = _profileManager.Delete(Profiles, profile);
            Log(LocalizedText.Format(
                profile.IsStandalone ? Strings.Log_ProfileDeletedFormat : Strings.Log_ProfileWorkspaceDeletedFormat,
                profile.Name));
            _ = SaveAsync();
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_ProfileDeleteFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(Strings.Profile_DeleteFailed, ex.Message);
        }
    }

    private void OpenProfileFolder()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            var path = _profileManager.GetProfileFolderPath(SelectedProfile)
                ?? throw new DirectoryNotFoundException(Strings.Profile_StandaloneFolderMissing);

            Directory.CreateDirectory(path);
            DialogService.OpenFolder(path);
        }
        catch (Exception ex)
        {
            Log(LocalizedText.Format(Strings.Log_OpenProfileFolderFailedFormat, ex.Message), LauncherLogLevel.ErrorsOnly);
        }
    }

    private ProfileExecutableSelection? TryGetExecutableSelection(string selectedPath)
    {
        if (SelectedProfile is null)
        {
            return null;
        }

        return ProfileExecutableSourceResolver.TryCreateSelection(
            SelectedProfile,
            selectedPath,
            includeWorkspace: true);
    }

    public ProfileSettingsViewModel? CreateProfileSettingsViewModel()
    {
        if (SelectedProfile is null)
        {
            return null;
        }

        var profile = SelectedProfile;
        return new ProfileSettingsViewModel(
            profile,
            _dialogService,
            () => SaveOrThrowAsync(),
            selectedPath => ProfileExecutableSourceResolver.TryCreateSelection(
                profile,
                selectedPath,
                includeWorkspace: true),
            () => ProfileExecutableSourceResolver.DetectAutomaticSelection(
                profile,
                includeWorkspace: false),
            name => Profiles.Any(p => p != profile && p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)),
            paths: _paths);
    }
}
