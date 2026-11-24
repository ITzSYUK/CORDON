using System.Text;
using System.Text.RegularExpressions;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class SavedGameLaunch
{
    private static readonly Regex StartupCommand = new(@"-(?:start|load)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string GetSaveName(SavedGameCatalog catalog, string filePath)
    {
        if (catalog.Extension is null)
            throw new InvalidOperationException(Strings.Saves_UseGameMenu);

        if (!catalog.Extension.Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase) || !catalog.Contains(filePath))
            throw new FileNotFoundException(Strings.Saves_FileUnavailable);

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() ||
            name.IndexOfAny(['(', ')']) >= 0 || name.Any(char.IsControl) || StartupCommand.IsMatch(name) ||
            Encoding.UTF8.GetByteCount(name) > 230)
        {
            throw new InvalidDataException(Strings.Saves_UnsupportedName);
        }

        return name;
    }

    public static string AppendArguments(string arguments, string saveName, string? engineCommandLinePath = null)
    {
        if (StartupCommand.IsMatch(GetEffectiveEngineArguments(arguments, engineCommandLinePath)))
            throw new InvalidOperationException(Strings.Saves_ConflictingArguments);

        var start = $"-start server({saveName}/single/alife/load) client(localhost)";
        return string.IsNullOrWhiteSpace(arguments) ? start : $"{arguments.TrimEnd()} {start}";
    }

    private static string GetEffectiveEngineArguments(string arguments, string? engineCommandLinePath) =>
        engineCommandLinePath is null ? arguments : $"{arguments} {string.Join(' ', File.ReadLines(engineCommandLinePath))}";

    internal static string? FindEngineCommandLine(
        string executable, string executablePath, string workingDirectory, Func<string, string?> findFile)
    {
        if (!IsAnomalyEngine(executable)) return null;

        var parent = Path.GetDirectoryName(Path.GetDirectoryName(executablePath));
        return (parent is null ? null : findFile(Path.Combine(parent, "commandline.txt")))
            ?? findFile(Path.Combine(workingDirectory, "commandline.txt"));
    }

    private static bool IsAnomalyEngine(string executable) =>
        AnomalyUsvfsEngineSelection.TryParseRelativePath(Path.Combine("bin", Path.GetFileName(executable)), out _, out _) ||
        System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileDescription?.Contains("X-Ray Monolith", StringComparison.OrdinalIgnoreCase) == true;

    internal static string? ResolveSaveExtension(string executable, string? modulePath, string arguments, Func<string?>? findOpenXrayConfig = null)
    {
        if (IsAnomalyEngine(executable)) return ".scop";

        var image = File.ReadAllBytes(executable).AsSpan();
        var isOpenXRay = image.IndexOf("OpenXRay"u8) >= 0;
        var (hasSav, hasScop) = GetFormats(image);
        if (!hasSav && !hasScop && modulePath is not null &&
            (image.IndexOf("xrGame.dll\0"u8) >= 0 || image.IndexOf("xrgame.dll\0"u8) >= 0 ||
             image.IndexOf("\0xrGame\0"u8) >= 0 ||
             System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileDescription?.Contains("X-Ray", StringComparison.OrdinalIgnoreCase) == true))
        {
            image = File.ReadAllBytes(modulePath).AsSpan();
            (hasSav, hasScop) = GetFormats(image);
        }

        if (hasSav && hasScop)
        {
            // OpenXRay includes both formats and selects one at startup.
            if (!isOpenXRay && image.IndexOf("OpenXRay"u8) < 0) return null;
            if (arguments.Contains("-shoc", StringComparison.Ordinal) || arguments.Contains("-soc", StringComparison.Ordinal) ||
                arguments.Contains("-cs", StringComparison.Ordinal)) return ".sav";
            if (arguments.Contains("-cop", StringComparison.Ordinal) || arguments.Contains("-unlock_game_mode", StringComparison.Ordinal)) return ".scop";
            if (findOpenXrayConfig?.Invoke() is { } openXrayConfig)
            {
                var mode = File.ReadLines(openXrayConfig, XRayTextEncoding.Config)
                    .Select(line => line.Split(';', 2)[0].Trim())
                    .SkipWhile(line => !line.Equals("[compatibility]", StringComparison.OrdinalIgnoreCase))
                    .Skip(1)
                    .TakeWhile(line => !line.StartsWith('['))
                    .Select(line => Regex.Match(line, @"^game_mode\s*=\s*(\w+)", RegexOptions.IgnoreCase))
                    .FirstOrDefault(match => match.Success)?.Groups[1].Value.ToLowerInvariant();
                return mode is "soc" or "shoc" or "cs" ? ".sav" : ".scop";
            }
            return ".scop";
        }

        return hasSav ? ".sav" : hasScop ? ".scop" : null;

        static (bool Sav, bool Scop) GetFormats(ReadOnlySpan<byte> data) =>
            (data.IndexOf("\0.sav\0"u8) >= 0 || data.IndexOf("\0*.sav\0"u8) >= 0,
             data.IndexOf("\0.scop\0"u8) >= 0 || data.IndexOf("\0*.scop\0"u8) >= 0);
    }

    public static string? TryResolveAnomalyEngine(Func<string, string?> findFile)
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

        return null;
    }

    public static string ResolvePhysicalEngine(LaunchPlan plan, bool allowUnconfiguredLauncher = false)
    {
        var executable = plan.ExecutablePath;
        if (AnomalyLauncherLocator.IsLauncherExecutable(executable) ||
            AnomalyLauncherLocator.IsBaseGameLauncher(plan.WorkingDirectory, executable))
        {
            var root = Path.GetDirectoryName(executable)!;
            var relative = TryResolveAnomalyEngine(path => SavedGameCatalog.ExistingFile(Path.Combine(root, path)));
            if (relative is null)
                return allowUnconfiguredLauncher ? executable : throw new InvalidOperationException(Strings.Saves_AnomalyEngineUnknown);

            var engine = Path.Combine(root, relative);
            if (File.Exists(engine)) return engine;
            if (!allowUnconfiguredLauncher)
                throw new FileNotFoundException(LocalizedText.Format(Strings.Ready_ExecutableMissingFormat, engine));
        }
        return executable;
    }

    public static LaunchPlan ApplyToPhysicalPlan(LaunchPlan plan, string savePath)
    {
        var executable = ResolvePhysicalEngine(plan);
        var catalog = SavedGameCatalog.ForPhysicalPlan(plan, executable);
        var saveName = GetSaveName(catalog, savePath);
        var commandLine = FindEngineCommandLine(executable, executable, plan.WorkingDirectory, SavedGameCatalog.ExistingFile);
        return new LaunchPlan(
            plan.BackendKind, executable, AppendArguments(plan.Arguments, saveName, commandLine), plan.WorkingDirectory,
            plan.RuntimeLease, plan.ProcessStarter, plan.RuntimeCompletion, plan.ActiveProcessIds);
    }
}
