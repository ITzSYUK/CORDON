using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public static class ScreenshotScannerService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".jpeg",
        ".jpg",
        ".png"
    };

    public static Task<IReadOnlyList<string>> ScanAsync(
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(
            () => Scan(profile, cancellationToken),
            cancellationToken);
    }

    private static List<string> Scan(
        ModProfile profile,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>(ProfileDataPathResolver.GetScreenshotDirectories(profile));
        var gamePath = profile.GameInstallPath;

        if (profile.IsStandalone && !string.IsNullOrWhiteSpace(gamePath))
        {
            directories.Add(Path.Combine(gamePath, "userdata", "screenshots"));
            directories.Add(Path.Combine(gamePath, "appdata", "screenshots"));
        }

        var results = new List<string>();
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    {
                        results.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A single unavailable screenshot directory must not prevent viewing the others.
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }
}
