using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Models;

public sealed class LaunchPlan
{
    public LaunchPlan(
        LaunchBackendKind backendKind,
        string executablePath,
        string? arguments,
        string workingDirectory,
        IAsyncDisposable? runtimeLease = null,
        Func<LaunchPlan, IProgress<string>?, System.Diagnostics.Process>? processStarter = null,
        Func<Task<int>>? runtimeCompletion = null,
        Func<IReadOnlyList<int>>? activeProcessIds = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException(Strings.Error_LaunchExecutablePathEmpty, nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException(Strings.Error_LaunchWorkingDirectoryEmpty, nameof(workingDirectory));
        }

        BackendKind = backendKind;
        ExecutablePath = executablePath;
        Arguments = arguments?.Trim() ?? string.Empty;
        WorkingDirectory = workingDirectory;
        RuntimeLease = runtimeLease;
        ProcessStarter = processStarter;
        RuntimeCompletion = runtimeCompletion;
        ActiveProcessIds = activeProcessIds;
    }

    public LaunchBackendKind BackendKind { get; }
    public string ExecutablePath { get; }
    public string Arguments { get; }
    public string WorkingDirectory { get; }
    public IAsyncDisposable? RuntimeLease { get; }
    public Func<LaunchPlan, IProgress<string>?, System.Diagnostics.Process>? ProcessStarter { get; }
    public Func<Task<int>>? RuntimeCompletion { get; }
    public Func<IReadOnlyList<int>>? ActiveProcessIds { get; }
}
