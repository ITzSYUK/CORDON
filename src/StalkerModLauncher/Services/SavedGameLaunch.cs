using System.Text;
using System.Text.RegularExpressions;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class SavedGameLaunch
{
    private static readonly Regex StartupCommand = new(@"(?:^|\s)-(?:start|load)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IEnumerable<FileInfo> Enumerate(IReadOnlyList<string> directories) =>
        directories
            .Where(Directory.Exists)
            .SelectMany(directory => new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions
            {
                IgnoreInaccessible = true
            }))
            .Where(file => IsSaveExtension(file.Extension))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

    public static string GetSaveName(ModProfile profile, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!IsSaveExtension(Path.GetExtension(filePath)) ||
            !ProfileDataPathResolver.GetSavedGameDirectories(profile).Contains(directory, StringComparer.OrdinalIgnoreCase) ||
            !File.Exists(filePath))
        {
            throw new FileNotFoundException(Strings.Saves_FileUnavailable);
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() ||
            name.IndexOfAny(['(', ')']) >= 0 || name.Any(char.IsControl) || StartupCommand.IsMatch(name) ||
            Encoding.UTF8.GetByteCount(name) > 230)
        {
            throw new InvalidDataException(Strings.Saves_UnsupportedName);
        }

        return name;
    }

    public static string AppendArguments(string arguments, string saveName)
    {
        if (StartupCommand.IsMatch(arguments))
            throw new InvalidOperationException(Strings.Saves_ConflictingArguments);

        var start = $"-start server({saveName}/single/alife/load) client(localhost)";
        return string.IsNullOrWhiteSpace(arguments) ? start : $"{arguments.TrimEnd()} {start}";
    }

    public static bool IsSwmLauncher(string executablePath) =>
        string.Equals(Path.GetFileName(executablePath), "swm_launcher.exe", StringComparison.OrdinalIgnoreCase);

    public static string ResolveAnomalyEngine(Func<string, string?> findFile)
    {
        var settingsPath = findFile("launcherconfig.toml");
        if (settingsPath is not null)
        {
            var settings = File.ReadLines(settingsPath)
                .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .ToArray();
            var renderer = settings.FirstOrDefault(parts => parts[0] == "renderer")?[1].Trim('"');
            var avx = settings.FirstOrDefault(parts => parts[0] == "use_avx")?[1];
            if (renderer is not null && AnomalyUsvfsEngineSelection.Renderers.Contains(renderer, StringComparer.OrdinalIgnoreCase))
                return AnomalyUsvfsEngineSelection.CreateRelativePath(renderer, avx == "true");
        }

        var legacyPath = findFile("AnomalyLauncher.cfg");
        if (legacyPath is not null)
        {
            var settings = File.ReadLines(legacyPath).Take(2).Select(line => line.Trim()).ToArray();
            if (settings.Length == 2 && AnomalyUsvfsEngineSelection.Renderers.Contains(settings[0], StringComparer.OrdinalIgnoreCase))
                return AnomalyUsvfsEngineSelection.CreateRelativePath(settings[0], settings[1].Equals("AVX", StringComparison.OrdinalIgnoreCase));
        }

        throw new InvalidOperationException(Strings.Saves_AnomalyEngineUnknown);
    }

    public static LaunchPlan ApplyToPhysicalPlan(LaunchPlan plan, string saveName)
    {
        var executable = plan.ExecutablePath;
        var arguments = plan.Arguments;
        if (IsSwmLauncher(executable))
            throw new InvalidOperationException(Strings.Saves_SwmRequiresLauncher);

        if (AnomalyLauncherLocator.IsLauncherExecutable(executable) ||
            AnomalyLauncherLocator.IsBaseGameLauncher(plan.WorkingDirectory, executable))
        {
            var root = Path.GetDirectoryName(executable)!;
            string? FindFile(string relativePath)
            {
                var path = Path.Combine(root, relativePath);
                return File.Exists(path) ? path : null;
            }

            executable = Path.Combine(root, ResolveAnomalyEngine(FindFile));
            if (FindFile("commandline.txt") is { } commandLine)
                arguments = $"{File.ReadAllText(commandLine).Trim()} {arguments}".Trim();
        }

        if (!File.Exists(executable))
            throw new FileNotFoundException(LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, executable));

        return new LaunchPlan(
            plan.BackendKind, executable, AppendArguments(arguments, saveName), plan.WorkingDirectory,
            plan.RuntimeLease, plan.ProcessStarter, plan.RuntimeCompletion, plan.ActiveProcessIds);
    }

    private static bool IsSaveExtension(string extension) =>
        extension.Equals(".sav", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".scop", StringComparison.OrdinalIgnoreCase);
}
