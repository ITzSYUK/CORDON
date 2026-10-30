using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class ProfileTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Export(string filePath, ModProfile profile)
    {
        var exported = ToExportedProfile(profile);
        File.WriteAllText(filePath, JsonSerializer.Serialize(exported, JsonOptions));
    }

    public static ModProfile Import(string filePath)
    {
        var json = File.ReadAllText(filePath);
        ExportedProfile? exported;
        try
        {
            exported = JsonSerializer.Deserialize<ExportedProfile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Strings.Profile_ImportInvalid, ex);
        }

        if (exported is null || exported.Name is null || exported.Mods is null ||
            !Enum.IsDefined(exported.LaunchBackendKind) ||
            exported.CollapsedModGroups?.Any(name => name is null) == true ||
            exported.Mods.Any(mod => mod is null || mod.Name is null || mod.SourcePath is null ||
                                     mod.ExcludedFiles?.Any(path => path is null) == true))
        {
            throw new InvalidDataException(Strings.Profile_ImportInvalid);
        }

        var validation = ProfileSettingsValidator.Validate(exported.Name, exported.ExecutableRelativePath, _ => false);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Messages));
        }

        return ToModProfile(exported);
    }

    internal static ExportedProfile ToExportedProfile(ModProfile profile)
    {
        return new ExportedProfile
        {
            Name = profile.Name,
            IsEnabled = profile.IsEnabled,
            IsDiscordStatusEnabled = profile.IsDiscordStatusEnabled,
            IsStandalone = profile.IsStandalone,
            UseBaseGameData = profile.UseBaseGameData,
            LaunchBackendKind = profile.LaunchBackendKind,
            ExecutableRelativePath = profile.ExecutableRelativePath,
            ExecutableSourcePath = profile.ExecutableSourcePath,
            FsgameSourcePath = profile.FsgameSourcePath,
            UsvfsExecutableOverrideRelativePath = profile.UsvfsExecutableOverrideRelativePath,
            LaunchArguments = profile.LaunchArguments,
            WorkingDirectoryRelative = profile.WorkingDirectoryRelative,
            GameInstallPath = profile.GameInstallPath,
            Mo2OverwritePath = profile.Mo2OverwritePath,
            CollapsedModGroups = [.. profile.CollapsedModGroups],
            Mods = profile.Mods.Select(mod => new ExportedMod
            {
                Name = mod.Name,
                SourcePath = mod.SourcePath,
                GroupName = mod.GroupName,
                IsEnabled = mod.IsEnabled,
                ExcludedFiles = [.. mod.ExcludedFiles],
                Order = mod.Order
            }).ToList()
        };
    }

    internal static ModProfile ToModProfile(ExportedProfile exported)
    {
        var profile = new ModProfile
        {
            Name = exported.Name.Trim(),
            IsEnabled = exported.IsEnabled,
            IsDiscordStatusEnabled = exported.IsDiscordStatusEnabled,
            IsStandalone = exported.IsStandalone,
            UseBaseGameData = exported.UseBaseGameData,
            LaunchBackendKind = exported.LaunchBackendKind,
            ExecutableRelativePath = exported.ExecutableRelativePath,
            ExecutableSourcePath = exported.ExecutableSourcePath ?? string.Empty,
            FsgameSourcePath = exported.FsgameSourcePath ?? string.Empty,
            UsvfsExecutableOverrideRelativePath = exported.UsvfsExecutableOverrideRelativePath ?? string.Empty,
            LaunchArguments = exported.LaunchArguments ?? string.Empty,
            WorkingDirectoryRelative = exported.WorkingDirectoryRelative ?? string.Empty,
            GameInstallPath = exported.GameInstallPath ?? string.Empty,
            Mo2OverwritePath = exported.Mo2OverwritePath ?? string.Empty,
            CollapsedModGroups = exported.CollapsedModGroups ?? []
        };

        foreach (var exportedMod in exported.Mods.OrderBy(mod => mod.Order))
        {
            profile.Mods.Add(new ModEntry
            {
                Name = exportedMod.Name,
                SourcePath = exportedMod.SourcePath,
                GroupName = exportedMod.GroupName ?? string.Empty,
                IsEnabled = exportedMod.IsEnabled,
                ExcludedFiles = exportedMod.ExcludedFiles ?? [],
                Order = profile.Mods.Count + 1
            });
        }

        return profile;
    }
}
