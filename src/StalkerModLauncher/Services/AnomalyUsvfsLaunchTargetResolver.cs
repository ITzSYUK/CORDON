using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed record UsvfsLaunchTarget(
    string ExecutablePath,
    string ExecutableRelativePath,
    string Arguments,
    string WorkingDirectory,
    string SourceName,
    bool BypassedLauncher,
    string? EngineCommandLinePath = null);

internal static class AnomalyUsvfsLaunchTargetResolver
{
    public static UsvfsLaunchTarget Resolve(
        ModProfile profile,
        FileLayerPlan layerPlan,
        LaunchPlanResolution launchResolution,
        bool forSavedGame = false,
        bool allowUnconfiguredLauncher = false)
    {
        var plan = launchResolution.Plan
            ?? throw new InvalidOperationException(Strings.Error_UsvfsLaunchPlanNotReady);
        var executable = launchResolution.Executable
            ?? throw new InvalidOperationException(Strings.Error_UsvfsExecutableNotReady);

        var isLauncher = AnomalyLauncherLocator.IsBaseGameLauncher(
                             profile.GameInstallPath,
                             plan.ExecutablePath) ||
                         AnomalyLauncherLocator.IsLauncherExecutable(plan.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(profile.UsvfsExecutableOverrideRelativePath))
        {
            if (!AnomalyUsvfsEngineSelection.TryParseRelativePath(
                    profile.UsvfsExecutableOverrideRelativePath,
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    LocalizedText.Format(Strings.Usvfs_InvalidAnomalyEngineFormat, profile.UsvfsExecutableOverrideRelativePath));
            }

            var selectedEngine = layerPlan.FindFinalFile(profile.UsvfsExecutableOverrideRelativePath)
                ?? throw new FileNotFoundException(
                    LocalizedText.Format(Strings.Usvfs_AnomalyEngineMissingFormat, profile.UsvfsExecutableOverrideRelativePath));
            return CreateEngineTarget(layerPlan, plan, selectedEngine, isLauncher, forSavedGame);
        }

        if (forSavedGame && isLauncher)
        {
            var enginePath = SavedGameLaunch.TryResolveAnomalyEngine(relativePath => FindProfileFile(layerPlan, relativePath));
            if (enginePath is not null && layerPlan.FindFinalFile(enginePath) is { } engine)
                return CreateEngineTarget(layerPlan, plan, engine, bypassedLauncher: true, forSavedGame: true);

            if (!allowUnconfiguredLauncher)
                throw new InvalidOperationException(enginePath is null ? Strings.Saves_AnomalyEngineUnknown
                    : LocalizedText.Format(Strings.Usvfs_AnomalyEngineMissingFormat, enginePath));
        }

        // The official USVFS hook follows child processes and uses its cross-bitness
        // proxy when this x86 launcher starts an x64 Anomaly engine. A manually chosen
        // AnomalyDX executable above remains the explicit direct-launch option.
        return new UsvfsLaunchTarget(
            plan.ExecutablePath,
            executable.RelativePath,
            plan.Arguments,
            plan.WorkingDirectory,
            executable.SourceName,
            BypassedLauncher: false,
            EngineCommandLinePath: forSavedGame ? FindEngineCommandLine(layerPlan, plan, plan.ExecutablePath, executable.RelativePath) : null);
    }

    private static UsvfsLaunchTarget CreateEngineTarget(
        FileLayerPlan layerPlan,
        LaunchPlan plan,
        FileLayerFile engine,
        bool bypassedLauncher,
        bool forSavedGame)
    {
        var commandLine = forSavedGame
            ? FindEngineCommandLine(layerPlan, plan, engine.FullPath, engine.RelativePath)
            : FindProfileFile(layerPlan, "commandline.txt");
        var launcherArguments = forSavedGame || commandLine is null
            ? string.Empty
            : File.ReadAllText(commandLine).Trim();

        return new UsvfsLaunchTarget(
            engine.FullPath,
            engine.RelativePath,
            CombineArguments(launcherArguments, plan.Arguments),
            plan.WorkingDirectory,
            engine.SourceName,
            bypassedLauncher,
            EngineCommandLinePath: forSavedGame ? commandLine : null);
    }

    private static string? FindEngineCommandLine(FileLayerPlan layerPlan, LaunchPlan plan, string executable, string relativeExe) =>
        SavedGameLaunch.FindEngineCommandLine(executable, relativeExe,
            Path.GetRelativePath(layerPlan.BaseGame.RootPath, plan.WorkingDirectory), path =>
                FindProfileFile(layerPlan, path.StartsWith(@".\", StringComparison.Ordinal) ? path[2..] : path));

    internal static string? FindProfileFile(FileLayerPlan layerPlan, string relativePath)
    {
        var stored = ProfileWritableGameFileStore.GetStoredFilePath(
            Path.GetDirectoryName(layerPlan.UserData.RootPath)!, relativePath);
        return File.Exists(stored) ? stored : layerPlan.FindFinalFile(relativePath)?.FullPath;
    }

    private static string CombineArguments(string first, string second)
    {
        return string.Join(
            " ",
            new[] { first.Trim(), second.Trim() }.Where(value => value.Length > 0));
    }
}
