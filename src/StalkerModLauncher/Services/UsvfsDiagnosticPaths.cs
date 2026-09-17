using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class UsvfsDiagnosticPaths
{
    private const string DiagnosticDirectoryName = "diagnostics";
    private const string LogFileName = "usvfs.log";
    private const string OldLogFileName = "usvfs.old.log";

    public static string Resolve(string profileWorkspace)
    {
        return Path.Combine(profileWorkspace, DiagnosticDirectoryName, LogFileName);
    }

    public static string Prepare(string profileWorkspace, IProgress<string>? progress = null)
    {
        var diagnosticDirectory = Path.Combine(profileWorkspace, DiagnosticDirectoryName);
        Directory.CreateDirectory(diagnosticDirectory);
        MoveLegacy(
            Path.Combine(profileWorkspace, "userdata", "logs", LogFileName),
            Path.Combine(diagnosticDirectory, LogFileName),
            Path.Combine(diagnosticDirectory, "usvfs.legacy.log"),
            progress);
        MoveLegacy(
            Path.Combine(profileWorkspace, "userdata", "logs", OldLogFileName),
            Path.Combine(diagnosticDirectory, OldLogFileName),
            Path.Combine(diagnosticDirectory, "usvfs.legacy.old.log"),
            progress);
        return Path.Combine(diagnosticDirectory, LogFileName);
    }

    private static void MoveLegacy(
        string sourcePath,
        string preferredDestinationPath,
        string fallbackDestinationPath,
        IProgress<string>? progress)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var destinationPath = File.Exists(preferredDestinationPath)
            ? fallbackDestinationPath
            : preferredDestinationPath;
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
            progress?.Report(LocalizedText.Format(Strings.Progress_UsvfsDiagnosticMovedFormat, destinationPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                LocalizedText.Format(Strings.Usvfs_LogCleanupFailedFormat, sourcePath),
                ex);
        }
    }
}
