using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public static class GameInstallationValidator
{
    public static ValidationResult Validate(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return Invalid("Папка игры не выбрана.", "Выберите папку с установленной игрой.");
        }

        if (!Directory.Exists(gamePath))
        {
            return Invalid("Папка игры не существует.", gamePath);
        }

        return new ValidationResult
        {
            IsValid = true,
            Summary = "Папка базовой игры доступна.",
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
