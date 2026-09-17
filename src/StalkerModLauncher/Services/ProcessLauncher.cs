using System.ComponentModel;
using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public interface ILaunchPlanExecutor
{
    Process Start(LaunchPlan plan, IProgress<string>? progress = null);
}

public sealed class LaunchPlanExecutor : ILaunchPlanExecutor
{
    public Process Start(LaunchPlan plan, IProgress<string>? progress = null)
    {
        if (plan.ProcessStarter is not null)
        {
            return plan.ProcessStarter(plan, progress);
        }

        return ProcessLauncher.Start(plan);
    }
}

public static class ProcessLauncher
{
    private const int ErrorElevationRequired = 740;

    public static Process Start(LaunchPlan plan)
    {
        return Start(plan.ExecutablePath, plan.Arguments, plan.WorkingDirectory);
    }

    public static Process Start(string executablePath, string? launchArguments, string workingDir)
    {
        try
        {
            return StartCore(executablePath, launchArguments, workingDir, useShellExecute: false, verb: null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            return StartCore(executablePath, launchArguments, workingDir, useShellExecute: true, verb: "runas");
        }
    }

    private static Process StartCore(
        string executablePath,
        string? launchArguments,
        string workingDir,
        bool useShellExecute,
        string? verb)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = launchArguments?.Trim() ?? string.Empty,
            WorkingDirectory = workingDir,
            UseShellExecute = useShellExecute
        };

        if (!string.IsNullOrWhiteSpace(verb))
        {
            startInfo.Verb = verb;
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException(Strings.Error_WindowsDidNotStartGame);
        }

        return process;
    }
}
