using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using System.Collections.ObjectModel;

namespace StalkerModLauncher.Services;

public sealed class ProfileManager
{
    private readonly AppPaths _paths;
    private readonly IProfileWorkspaceManager _workspaceManager;

    public ProfileManager(AppPaths paths, IProfileWorkspaceManager workspaceManager)
    {
        _paths = paths;
        _workspaceManager = workspaceManager;
    }

    public ModProfile Create(IReadOnlyCollection<ModProfile> profiles)
    {
        var profile = new ModProfile
        {
            Name = GetUniqueName(profiles, LocalizedText.Format(Strings.Profile_DefaultNameFormat, profiles.Count + 1)),
            Description = Strings.Profile_DefaultDescription,
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };

        profile.ModInstallPath = _paths.GetDefaultModInstallPath(profile.GameInstallPath);
        return profile;
    }

    public ModProfile Duplicate(IReadOnlyCollection<ModProfile> profiles, ModProfile source)
    {
        var duplicate = new ModProfile
        {
            Name = GetUniqueName(profiles, LocalizedText.Format(Strings.Profile_CopyNameFormat, source.Name)),
            Description = source.Description,
            Badge = source.Badge,
            IsEnabled = source.IsEnabled,
            IsDiscordStatusEnabled = source.IsDiscordStatusEnabled,
            IsStandalone = source.IsStandalone,
            UseBaseGameData = source.UseBaseGameData,
            LaunchBackendKind = source.LaunchBackendKind,
            LaunchArguments = source.LaunchArguments,
            ExecutableRelativePath = source.ExecutableRelativePath,
            ExecutableSourcePath = source.ExecutableSourcePath,
            FsgameSourcePath = source.FsgameSourcePath,
            UsvfsExecutableOverrideRelativePath = source.UsvfsExecutableOverrideRelativePath,
            WorkingDirectoryRelative = source.WorkingDirectoryRelative,
            GameInstallPath = source.GameInstallPath,
            Mo2OverwritePath = source.Mo2OverwritePath,
            CollapsedModGroups = [.. source.CollapsedModGroups]
        };

        foreach (var sourceMod in source.Mods.OrderBy(mod => mod.Order))
        {
            duplicate.Mods.Add(new ModEntry
            {
                Name = sourceMod.Name,
                SourcePath = sourceMod.SourcePath,
                GroupName = sourceMod.GroupName,
                IsEnabled = sourceMod.IsEnabled,
                ExcludedFiles = [.. sourceMod.ExcludedFiles],
                Order = duplicate.Mods.Count + 1
            });
        }

        duplicate.ModInstallPath = _paths.GetDefaultModInstallPath(duplicate.GameInstallPath);

        return duplicate;
    }

    public void PrepareImported(IReadOnlyCollection<ModProfile> profiles, ModProfile profile)
    {
        EnsureDefaults(profile);
        profile.Name = GetUniqueName(profiles, profile.Name);
    }

    public static bool MoveToInsertionIndex(
        ObservableCollection<ModProfile> profiles,
        ModProfile profile,
        int insertionIndex)
    {
        return CollectionReorderer.MoveToInsertionIndex(profiles, profile, insertionIndex);
    }

    public void EnsureDefaults(ModProfile profile)
    {
        profile.IsRunning = false;
        if (profile.SettingsLoadError.Length > 0) return;

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(profile.ExecutableRelativePath))
        {
            profile.ExecutableRelativePath = @"bin\xr_3da.exe";
        }

        var defaultModInstallPath = _paths.GetDefaultModInstallPath(profile.GameInstallPath);
        var legacyModInstallPath = _paths.GetLegacyProfileModInstallPath(profile.Id, profile.GameInstallPath);
        if (string.IsNullOrWhiteSpace(profile.ModInstallPath) ||
            FileSystemSafety.IsSameDirectory(profile.ModInstallPath, legacyModInstallPath))
        {
            profile.ModInstallPath = defaultModInstallPath;
        }
    }

    internal async Task<(ModProfile? SelectedProfile, Exception? CleanupFailure)> DeleteAsync(
        IList<ModProfile> profiles, ModProfile profile, Func<Task> persistSettings)
    {
        if (profile.IsRunning || profile.IsPreparing) throw new InvalidOperationException(Strings.ProfileStorage_Busy);
        var index = profiles.IndexOf(profile);
        if (index < 0) return (profiles.FirstOrDefault(), null);

        if (!profile.IsStandalone && profile.SettingsLoadError.Length == 0)
            _workspaceManager.ValidateProfileWorkspaceDeletion(profile, profile.GameInstallPath);

        profiles.RemoveAt(index);
        try
        {
            // The settings journal can fail midway. Do not touch workspace data until the save succeeds.
            await persistSettings();
        }
        catch
        {
            profiles.Insert(Math.Min(index, profiles.Count), profile);
            throw;
        }

        try
        {
            DeleteProfileData(profile);
            return (profiles.FirstOrDefault(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                        or ArgumentException or NotSupportedException)
        {
            return (profiles.FirstOrDefault(), ex);
        }
    }

    internal void DeleteProfileData(ModProfile profile)
    {
        if (!profile.IsStandalone && profile.SettingsLoadError.Length == 0)
        {
            var workspacePath = string.IsNullOrWhiteSpace(profile.WorkspacePath)
                ? CreateWorkspacePath(profile, profile.GameInstallPath)
                : profile.WorkspacePath;
            _workspaceManager.DeleteProfileWorkspace(profile, profile.GameInstallPath);
            LegacyProfileDataAlias.Delete(workspacePath, profile.Id);
            UsvfsBootstrapPathResolver.DeleteProfile(
                workspacePath,
                profile.GameInstallPath,
                profile.Id);
        }
    }

    public string? GetProfileFolderPath(ModProfile profile)
    {
        if (profile.SettingsLoadError.Length > 0) return null;
        if (profile.IsStandalone)
        {
            return profile.Mods
                .FirstOrDefault(mod => mod.IsEnabled && Directory.Exists(mod.SourcePath))
                ?.SourcePath;
        }

        if (!string.IsNullOrWhiteSpace(profile.WorkspacePath))
        {
            return profile.WorkspacePath;
        }

        return CreateWorkspacePath(profile, profile.GameInstallPath);
    }

    public string EnsureProfileFolderPath(ModProfile profile, IProgress<string>? progress = null)
    {
        if (profile.SettingsLoadError.Length > 0) throw new InvalidOperationException(profile.SettingsLoadError);
        if (profile.IsStandalone)
        {
            throw new InvalidOperationException(Strings.Profile_StandaloneNoWorkspace);
        }

        var workspacePath = _workspaceManager.EnsureProfileWorkspace(
            profile,
            profile.GameInstallPath,
            progress);
        profile.WorkspacePath = workspacePath;
        return workspacePath;
    }

    public static string GetUniqueName(IEnumerable<ModProfile> profiles, string requestedName)
    {
        var profileList = profiles.ToList();
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? LocalizedText.Format(Strings.Profile_DefaultNameFormat, profileList.Count + 1)
            : requestedName.Trim();
        var name = baseName;
        var counter = 1;
        while (profileList.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({++counter})";
        }

        return name;
    }

    private string CreateWorkspacePath(ModProfile profile, string gameInstallPath)
    {
        return Path.Combine(_paths.GetPreferredWorkspaceRoot(gameInstallPath), CreateWorkspaceDirectoryName(profile));
    }

    internal static string CreateWorkspaceDirectoryName(ModProfile profile)
    {
        var id = profile.Id.Trim();
        if (id.Length > 64 ||
            id.Length == 0 ||
            id.Any(character => character > 0x7F ||
                                !char.IsLetterOrDigit(character) &&
                                character is not '-' and not '_'))
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(profile.Id));
            id = Convert.ToHexString(bytes)[..16];
        }

        return $"profile-{id.ToLowerInvariant()}";
    }
}
