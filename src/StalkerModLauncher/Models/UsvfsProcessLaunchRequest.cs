namespace StalkerModLauncher.Models;

public sealed record UsvfsProcessLaunchRequest(
    string ExecutablePath,
    string? Arguments,
    string WorkingDirectory)
{
    public bool StartFromVirtualPath { get; init; }
}

public sealed record UsvfsProcessLaunchResult(
    int ExitCode,
    int ProcessId);
