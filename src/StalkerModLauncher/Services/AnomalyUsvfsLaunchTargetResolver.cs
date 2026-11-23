using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed record UsvfsLaunchTarget(
    string ExecutablePath,
    string ExecutableRelativePath,
    string Arguments,
    string WorkingDirectory,
    string SourceName,
    bool BypassedLauncher);

internal static class AnomalyUsvfsLaunchTargetResolver
{
    public static UsvfsLaunchTarget Resolve(
        ModProfile profile,
        FileLayerPlan layerPlan,
        LaunchPlanResolution launchResolution,
        string? saveName = null)
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
            return CreateEngineTarget(profile, layerPlan, plan, selectedEngine, isLauncher);
        }

        if (saveName is not null && isLauncher)
        {
            var enginePath = SavedGameLaunch.ResolveAnomalyEngine(relativePath => FindProfileFile(layerPlan, relativePath));
            var engine = layerPlan.FindFinalFile(enginePath)
                ?? throw new FileNotFoundException(LocalizedText.Format(Strings.Usvfs_AnomalyEngineMissingFormat, enginePath));
            return CreateEngineTarget(profile, layerPlan, plan, engine, BypassedLauncher: true);
        }

        if (!isLauncher)
        {
            return new UsvfsLaunchTarget(
                plan.ExecutablePath,
                executable.RelativePath,
                plan.Arguments,
                plan.WorkingDirectory,
                executable.SourceName,
                BypassedLauncher: false);
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
            BypassedLauncher: false);
    }

    private static UsvfsLaunchTarget CreateEngineTarget(
        ModProfile profile,
        FileLayerPlan layerPlan,
        LaunchPlan plan,
        FileLayerFile engine,
        bool BypassedLauncher)
    {
        var commandLine = FindProfileFile(layerPlan, "commandline.txt");
        var launcherArguments = commandLine is null
            ? string.Empty
            : File.ReadAllText(commandLine).Trim();

        return new UsvfsLaunchTarget(
            engine.FullPath,
            engine.RelativePath,
            CombineArguments(launcherArguments, profile.LaunchArguments),
            plan.WorkingDirectory,
            engine.SourceName,
            BypassedLauncher);
    }

    private static string? FindProfileFile(FileLayerPlan layerPlan, string relativePath)
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
