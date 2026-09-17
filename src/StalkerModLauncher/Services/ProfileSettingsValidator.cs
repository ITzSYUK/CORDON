using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class ProfileSettingsValidator
{
    public static ValidationResult Validate(string profileName, string executableRelativePath, Func<string, bool> isNameTaken)
    {
        var messages = new List<string>();
        var normalizedName = profileName.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            messages.Add(Strings.Validation_NameRequired);
        }
        else if (isNameTaken(normalizedName))
        {
            messages.Add(Strings.Validation_NameExists);
        }

        try
        {
            FileSystemSafety.EnsureRelativePath(executableRelativePath, Strings.SettingsProfile_Executable);
        }
        catch (Exception ex)
        {
            messages.Add(ex.Message);
        }

        return new ValidationResult
        {
            IsValid = messages.Count == 0,
            Summary = messages.Count == 0 ? Strings.Validation_Valid : messages[0],
            Messages = messages
        };
    }
}
