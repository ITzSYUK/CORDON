using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public static class GameInstallationValidator
{
    public static ValidationResult Validate(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return Invalid(Strings.Validation_GameNotSelected, Strings.Main_SelectGame);
        }

        if (!Directory.Exists(gamePath))
        {
            return Invalid(Strings.Validation_GameMissing, gamePath);
        }

        return new ValidationResult
        {
            IsValid = true,
            Summary = Strings.Validation_GameAvailable,
            Messages = new[] { gamePath }
        };

        static ValidationResult Invalid(string summary, string detail)
        {
            return new ValidationResult
            {
                IsValid = false,
                Summary = summary,
                Messages = new[] { detail }
            };
        }
    }
}
