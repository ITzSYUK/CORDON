using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed record ProfileWritableGameFileRule(
    string RelativePath,
    string StorageRelativePath,
    string Reason);

public static class ProfileWritableGameFiles
{
    public const string WritableGameFilesRootRelativePath = @"userdata\writable-game-files";
    public const string DefaultOverwriteRootRelativePath = @"userdata\overwrite";

    public static IReadOnlyList<ProfileWritableGameFileRule> Rules { get; } =
    [
        new(
            Path.Combine("gamedata", "configs", "localization.ltx"),
            Path.Combine(WritableGameFilesRootRelativePath, "gamedata", "configs", "localization.ltx"),
            Strings.Writable_LocalizationReason),
        new(
            Path.Combine("gamedata", "configs", "axr_options.ltx"),
            Path.Combine(WritableGameFilesRootRelativePath, "gamedata", "configs", "axr_options.ltx"),
            Strings.Writable_OptionsReason),
        new(
            "AnomalyLauncher.cfg",
            Path.Combine(WritableGameFilesRootRelativePath, "AnomalyLauncher.cfg"),
            Strings.Writable_LauncherSettingsReason),
        new(
            "commandline.txt",
            Path.Combine(WritableGameFilesRootRelativePath, "commandline.txt"),
            Strings.Writable_LauncherSettingsReason),
        new(
            "launcherconfig.toml",
            Path.Combine(WritableGameFilesRootRelativePath, "launcherconfig.toml"),
            Strings.Writable_LauncherSettingsReason)
    ];
}
