using System.ComponentModel;
using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public interface ILaunchPlanExecutor
{
    ProfileLaunchHandle Start(LaunchPlan plan, IProgress<string>? progress = null);
}

public sealed class LaunchPlanExecutor : ILaunchPlanExecutor
{
    public ProfileLaunchHandle Start(LaunchPlan plan, IProgress<string>? progress = null)
    {
        if (plan.ProcessStarter is not null)
        {
            return new ProfileLaunchHandle(plan.ProcessStarter(plan, progress), activeProcessIds: plan.ActiveProcessIds);
        }

        return ProcessLauncher.Start(plan);
    }
}

public static class ProcessLauncher
{
    private const int ErrorElevationRequired = 740;
    internal const string ElevatedLaunchArgument = "--run-elevated-game";
    private const string JobPrefix = @"Local\CORDON-Game-";

    public static ProfileLaunchHandle Start(LaunchPlan plan)
    {
        var job = WindowsGameJob.Create();
        try
        {
            return job.Track(job.Start(plan.ExecutablePath, plan.Arguments, plan.WorkingDirectory));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            job.Dispose();
            return StartElevated(plan);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static ProfileLaunchHandle StartElevated(LaunchPlan plan)
    {
        var jobName = JobPrefix + Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException(Strings.Startup_ExecutablePathMissing),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add(ElevatedLaunchArgument);
        startInfo.ArgumentList.Add(jobName);
        startInfo.ArgumentList.Add(plan.ExecutablePath);
        startInfo.ArgumentList.Add(plan.Arguments);
        startInfo.ArgumentList.Add(plan.WorkingDirectory);

        var job = WindowsGameJob.Create(jobName);
        try
        {
            var helper = Process.Start(startInfo) ?? throw new InvalidOperationException(Strings.Error_WindowsDidNotStartGame);
            return job.Track(helper);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static async Task<int> RunElevatedGameAsync(string[] arguments)
    {
        if (arguments is not [ElevatedLaunchArgument, var jobName, var executable, var commandLine, var workingDirectory] ||
            !jobName.StartsWith(JobPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(jobName[JobPrefix.Length..], "N", out _) ||
            !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(workingDirectory))
            throw new InvalidOperationException(Strings.Error_WindowsDidNotStartGame);

        using var job = WindowsGameJob.Open(jobName);
        using var game = job.Start(executable, commandLine, workingDirectory);
        return await job.WaitForCompletionAsync(game);
    }
}
