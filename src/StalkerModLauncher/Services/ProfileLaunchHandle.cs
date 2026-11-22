using System.Diagnostics;

namespace StalkerModLauncher.Services;

public sealed class ProfileLaunchHandle
{
    private readonly Func<IReadOnlyList<int>>? _activeProcessIds;
    private readonly Func<bool>? _terminate;
    private readonly bool _includeRootProcess;

    public ProfileLaunchHandle(
        Process process,
        Task<int>? completion = null,
        Func<IReadOnlyList<int>>? activeProcessIds = null,
        Func<bool>? terminate = null,
        bool includeRootProcess = true)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Completion = completion;
        _activeProcessIds = activeProcessIds;
        _terminate = terminate;
        _includeRootProcess = includeRootProcess;
    }

    public Process Process { get; }
    public int ProcessId => Process.Id;
    public Task<int>? Completion { get; }

    public IReadOnlyList<int> GetActiveProcessIds()
    {
        var ids = new HashSet<int>();
        try
        {
            foreach (var processId in _activeProcessIds?.Invoke() ?? [])
            {
                if (processId > 0)
                {
                    ids.Add(processId);
                }
            }
        }
        catch
        {
            // Readiness checks are diagnostic and must not affect the game session.
        }

        try
        {
            if (_includeRootProcess && !Process.HasExited)
            {
                ids.Add(Process.Id);
            }
        }
        catch (InvalidOperationException)
        {
        }

        return ids.ToArray();
    }

    public bool TryTerminate()
    {
        if (_terminate is not null) return _terminate();

        var terminated = false;
        foreach (var processId in GetActiveProcessIds().OrderByDescending(id => id))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                terminated = true;
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return terminated;
    }
}
