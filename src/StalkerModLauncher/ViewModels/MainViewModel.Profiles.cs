using Microsoft.Win32;
using StalkerModLauncher.Models;
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
            Title = "Экспорт профиля",
            Filter = "Profile files (*.stalkerprofile)|*.stalkerprofile|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{profile.Name}.stalkerprofile"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ProfileTransferService.Export(dialog.FileName, profile);
            Log($"Profile exported: {profile.Name}");
        }
        catch (Exception ex)
        {
            Log($"Export failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError("Ошибка экспорта", ex.Message);
        }
    }

    private void ImportProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт профиля",
            Filter = "Profile files (*.stalkerprofile)|*.stalkerprofile|JSON files (*.json)|*.json|All files (*.*)|*.*",
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
            Log($"Profile imported: {profile.Name}");
        }
        catch (Exception ex)
        {
            Log($"Import failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError("Ошибка импорта", ex.Message);
        }
    }

    private async Task ImportSettingsAsync()
    {
        var path = DialogService.PickFile(
            "Импорт настроек CORDON",
            "Файлы JSON (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        try
        {
            _autoSave.Cancel();
            await _settingsStore.ImportAsync(path);
            await LoadAsync();
            Log($"Settings imported: {path}");
        }
        catch (Exception ex)
        {
            Log($"Settings import failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError("Не удалось импортировать настройки", ex.Message);
        }
    }

    private void ChooseGameFolder()
    {
        var selected = DialogService.PickFolder("Choose S.T.A.L.K.E.R. GOG folder", GameInstallPath);
        if (selected is null)
        {
            return;
        }

        _lastBrowsedGamePath = selected;
        GameInstallPath = selected;
        RefreshValidation();
        Log($"Game folder selected: {selected}");
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
        Log($"Profile created: {profile.Name}");
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
            Log($"MO2 profile imported: {profile.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Profiles.Remove(profile);
            SelectedProfile = previousSelection is not null && Profiles.Contains(previousSelection)
                ? previousSelection
                : Profiles.FirstOrDefault();
            Log($"MO2 import rolled back: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError(
                "Не удалось перенести сборку MO2",
                $"Профиль не создан, изменения отменены.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
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
        Log($"Profile duplicated: {profile.Name}");
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
            ? $"Удалить профиль '{profile.Name}'? Файлы мода останутся нетронутыми."
            : $"Удалить профиль '{profile.Name}' вместе с его рабочей папкой, сохранениями и логами?";
        if (!DialogService.Confirm("Удалить профиль", deleteMessage))
        {
            return;
        }

        try
        {
            SelectedProfile = _profileManager.Delete(Profiles, profile);
            Log(profile.IsStandalone ? $"Profile deleted: {profile.Name}" : $"Profile and workspace deleted: {profile.Name}");
            _ = SaveAsync();
        }
        catch (Exception ex)
        {
            Log($"Profile delete failed: {ex.Message}", LauncherLogLevel.ErrorsOnly);
            _dialogService.ShowError("Не удалось удалить профиль", ex.Message);
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
                ?? throw new DirectoryNotFoundException("Папка включённой автономной сборки не найдена.");

            Directory.CreateDirectory(path);
            DialogService.OpenFolder(path);
        }
        catch (Exception ex)
        {
            Log($"Could not open profile folder: {ex.Message}", LauncherLogLevel.ErrorsOnly);
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
