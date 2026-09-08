using System.Text.Json;
using StalkerModLauncher.Models;

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
        var exported = JsonSerializer.Deserialize<ExportedProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Файл профиля пуст или имеет неверный формат.");

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
            LaunchBackendKind = LaunchBackendKind.LinkedWorkspace,
            ExecutableRelativePath = profile.ExecutableRelativePath,
            ExecutableSourcePath = profile.ExecutableSourcePath,
            UsvfsExecutableOverrideRelativePath = profile.UsvfsExecutableOverrideRelativePath,
            LaunchArguments = profile.LaunchArguments,
            WorkingDirectoryRelative = profile.WorkingDirectoryRelative,
            GameInstallPath = profile.GameInstallPath,
            Mo2OverwritePath = profile.Mo2OverwritePath,
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
            LaunchBackendKind = LaunchBackendKind.LinkedWorkspace,
            ExecutableRelativePath = exported.ExecutableRelativePath,
            ExecutableSourcePath = exported.ExecutableSourcePath ?? string.Empty,
            UsvfsExecutableOverrideRelativePath = exported.UsvfsExecutableOverrideRelativePath ?? string.Empty,
            LaunchArguments = exported.LaunchArguments,
            WorkingDirectoryRelative = exported.WorkingDirectoryRelative,
            GameInstallPath = exported.GameInstallPath,
            Mo2OverwritePath = exported.Mo2OverwritePath ?? string.Empty
        };

        foreach (var exportedMod in exported.Mods.OrderBy(mod => mod.Order))
        {
            profile.Mods.Add(new ModEntry
            {
                Name = exportedMod.Name,
                SourcePath = exportedMod.SourcePath,
                GroupName = exportedMod.GroupName,
                IsEnabled = exportedMod.IsEnabled,
                ExcludedFiles = exportedMod.ExcludedFiles ?? [],
                Order = profile.Mods.Count + 1
            });
        }

        return profile;
    }
}
