using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class ProfileReadinessService
{
    public static ValidationResult Validate(ModProfile? profile)
    {
        if (profile is null)
        {
            return GameInstallationValidator.Validate(string.Empty);
        }

        return profile.IsStandalone
            ? ValidateStandalone(profile)
            : ValidateOverlay(profile);
    }

    private static ValidationResult ValidateStandalone(ModProfile profile)
    {
        var enabledMods = profile.Mods.Where(mod => mod.IsEnabled).ToArray();
        var messages = new List<string>();
        if (!profile.IsEnabled)
        {
            messages.Add(Strings.Ready_ProfileDisabled);
        }

        if (enabledMods.Length != 1)
        {
            messages.Add(Strings.Ready_StandaloneModCount);
        }
        else if (!Directory.Exists(enabledMods[0].SourcePath))
        {
            messages.Add(LocalizedText.Format(Strings.Ready_ModFolderMissingFormat, enabledMods[0].Name));
        }

        var executableIsSafe = ValidateExecutablePath(profile, messages);
        var ready = profile.IsEnabled &&
                    enabledMods.Length == 1 &&
                    Directory.Exists(enabledMods[0].SourcePath) &&
                    executableIsSafe;
        return CreateResult(ready, ready ? Strings.Ready_Ready : string.Join(Environment.NewLine, messages), messages);
    }

    private static ValidationResult ValidateOverlay(ModProfile profile)
    {
        var gameValidation = GameInstallationValidator.Validate(profile.GameInstallPath);
        var messages = new List<string>(gameValidation.Messages);
        var enabledMods = profile.Mods.Where(mod => mod.IsEnabled).ToArray();
        var missingMods = enabledMods.Where(mod => !Directory.Exists(mod.SourcePath)).ToArray();
        if (!profile.IsEnabled)
        {
            messages.Add(Strings.Ready_ProfileDisabled);
        }

        foreach (var mod in missingMods)
        {
            messages.Add(LocalizedText.Format(Strings.Ready_ModFolderMissingFormat, mod.Name));
        }

        var overwriteExists = string.IsNullOrWhiteSpace(profile.Mo2OverwritePath) ||
                              Directory.Exists(profile.Mo2OverwritePath);
        if (!overwriteExists)
        {
            messages.Add(LocalizedText.Format(Strings.Ready_OverwriteMissingFormat, profile.Mo2OverwritePath));
        }

        var executableIsSafe = ValidateExecutablePath(profile, messages);
        var exclusionsAreValid = ValidateExcludedFiles(profile, enabledMods, messages);
        var fsgameIsValid = ValidateFsgamePath(profile, messages);
        var ready = gameValidation.IsValid &&
                    profile.IsEnabled &&
                    missingMods.Length == 0 &&
                    overwriteExists &&
                    exclusionsAreValid &&
                    fsgameIsValid &&
                    executableIsSafe;
        return CreateResult(ready, ready ? Strings.Ready_Ready : string.Join(Environment.NewLine, messages.Distinct()), messages);
    }

    private static bool ValidateExcludedFiles(
        ModProfile profile,
        IReadOnlyList<ModEntry> enabledMods,
        List<string> messages)
    {
        var valid = true;
        var orderedMods = enabledMods.OrderBy(mod => mod.Order).ToArray();
        foreach (var mod in orderedMods)
        {
            foreach (var excluded in mod.ExcludedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    FileSystemSafety.EnsureRelativePath(excluded, Strings.Safety_ExcludedModFile);
                    var hasOtherProvider = File.Exists(Path.Combine(profile.GameInstallPath, excluded)) ||
                                           orderedMods.Any(other =>
                                               !ReferenceEquals(other, mod) &&
                                               File.Exists(Path.Combine(other.SourcePath, excluded)) &&
                                               !other.ExcludedFiles.Contains(excluded, StringComparer.OrdinalIgnoreCase));
                    if (!hasOtherProvider)
                    {
                        messages.Add(LocalizedText.Format(Strings.Ready_ExcludedProviderMissingFormat, mod.Name, excluded));
                        valid = false;
                    }
                }
                catch (Exception ex)
                {
                    messages.Add(ex.Message);
                    valid = false;
                }
            }
        }

        return valid;
    }

    private static bool ValidateExecutablePath(ModProfile profile, List<string> messages)
    {
        try
        {
            FileSystemSafety.EnsureRelativePath(profile.ExecutableRelativePath, Strings.Safety_LaunchExecutable);
            if (profile.IsStandalone)
            {
                return true;
            }

            var roots = ProfileExecutableSourceResolver.GetSourceRoots(profile, includeWorkspace: false)
                .Select(root => new LaunchExecutableSearchRoot(
                    root.RootPath,
                    root.DisplayName,
                    root.Order,
                    root.IsBaseGameRoot))
                .ToArray();
            var executable = ProfileLaunchPlanResolver.ResolveExecutableSource(
                profile,
                roots,
                profile.ExecutableRelativePath,
                allowPinnedSource: true,
                allowDedicatedFallback: LaunchExecutableDetector.IsDedicatedExecutable(profile.ExecutableRelativePath));
            if (executable is { IsAvailable: true })
            {
                return true;
            }

            messages.Add(executable?.Reason ?? LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, profile.ExecutableRelativePath));
            return false;
        }
        catch (Exception ex)
        {
            messages.Add(ex.Message);
            return false;
        }
    }

    private static bool ValidateFsgamePath(ModProfile profile, List<string> messages)
    {
        try
        {
            var source = FileLayerPlan.ResolveFsgameSource(profile);
            if (source is not null)
            {
                ProfileDataConfigurator.ValidateFsgameSource(source.FullPath);
                return true;
            }

            var relativePath = FileLayerPlan.ResolveFsgameLaunchArgument(profile.LaunchArguments);
            messages.Add(relativePath is null
                ? Strings.Ready_FsgameMissing
                : LocalizedText.Format(Strings.Ready_FsltxMissingFormat, relativePath));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            messages.Add(ex.Message);
            return false;
        }
    }

    private static ValidationResult CreateResult(bool isValid, string summary, IReadOnlyList<string> messages)
    {
        return new ValidationResult { IsValid = isValid, Summary = summary, Messages = messages };
    }
}
