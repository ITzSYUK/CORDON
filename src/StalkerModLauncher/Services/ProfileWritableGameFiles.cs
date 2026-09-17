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
            Strings.Writable_OptionsReason)
    ];
}
