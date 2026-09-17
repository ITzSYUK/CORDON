using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class UsvfsRuntime(IUsvfsNativeApi nativeApi) : IUsvfsRuntime
{
    public IUsvfsRuntimeSession CreateSession(
        UsvfsMappingPlan mappingPlan,
        UsvfsRuntimeOptions options,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mappingPlan);
        ArgumentNullException.ThrowIfNull(options);

        var parameters = IntPtr.Zero;
        UsvfsLogCollector? logCollector = null;
        var reservation = UsvfsSessionReservation.Acquire();
        try
        {
            parameters = nativeApi.CreateParameters();
            ConfigureParameters(parameters, options);
            var collectDiagnostics = !string.IsNullOrWhiteSpace(options.DiagnosticLogPath);
            nativeApi.InitLogging(collectDiagnostics ? false : options.LogToConsole);
            if (collectDiagnostics)
            {
                logCollector = new UsvfsLogCollector(nativeApi, options.DiagnosticLogPath!);
            }

            if (!nativeApi.CreateVfs(parameters))
            {
                throw new InvalidOperationException(Strings.Error_UsvfsCreateFailed);
            }

            nativeApi.ClearVirtualMappings();
            ApplyMappings(mappingPlan, progress);
            return new UsvfsRuntimeSession(nativeApi, parameters, reservation, logCollector);
        }
        catch
        {
            try
            {
                logCollector?.Dispose();
                nativeApi.DisconnectVfs();
            }
            finally
            {
                nativeApi.FreeParameters(parameters);
                reservation.Dispose();
            }

            throw;
        }
    }

    public async Task<UsvfsProcessLaunchResult> RunAsync(
        UsvfsMappingPlan mappingPlan,
        UsvfsProcessLaunchRequest launchRequest,
        UsvfsRuntimeOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappingPlan);
        ArgumentNullException.ThrowIfNull(launchRequest);
        ArgumentNullException.ThrowIfNull(options);

        ValidateLaunchRequest(launchRequest);
        await using var session = CreateSession(mappingPlan, options, progress);
        var startedProcess = session.StartProcess(launchRequest, progress, cancellationToken);
        var exitCode = await session.GetExitCodeAsync(cancellationToken);
        return new UsvfsProcessLaunchResult(exitCode, startedProcess.Id);
    }

    private void ConfigureParameters(IntPtr parameters, UsvfsRuntimeOptions options)
    {
        nativeApi.SetInstanceName(parameters, options.InstanceName);
        nativeApi.SetDebugMode(parameters, options.DebugMode);
        nativeApi.SetLogLevel(parameters, UsvfsLogLevel.Debug);
        nativeApi.SetCrashDumpType(parameters, UsvfsCrashDumpType.None);
        nativeApi.SetCrashDumpPath(parameters, string.Empty);
    }

    internal static string BuildCommandLine(string executablePath, string? arguments)
    {
        var commandLine = Quote(executablePath);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            commandLine += " " + arguments.Trim();
        }

        return commandLine;
    }

    private void ApplyMappings(UsvfsMappingPlan mappingPlan, IProgress<string>? progress)
    {
        foreach (var operation in mappingPlan.Operations.OrderBy(operation => operation.Order))
        {
            ApplyMapping(operation, progress);
        }
    }

    private void ApplyMapping(UsvfsMappingOperation operation, IProgress<string>? progress)
    {
        var flags = UsvfsLinkOptions.Recursive;
        if (operation.MonitorChanges)
        {
            flags |= UsvfsLinkOptions.MonitorChanges;
        }

        if (operation.CreateTarget)
        {
            flags |= UsvfsLinkOptions.CreateTarget;
        }

        var ok = operation.Kind switch
        {
            UsvfsMappingKind.DirectoryStatic => nativeApi.LinkDirectoryStatic(
                operation.SourcePath,
                operation.DestinationPath,
                flags),
            UsvfsMappingKind.File => nativeApi.LinkFile(
                operation.SourcePath,
                operation.DestinationPath,
                flags),
            _ => throw new InvalidOperationException(LocalizedText.Format(Strings.Error_UsvfsMappingKindFormat, operation.Kind))
        };

        if (!ok)
        {
            throw new InvalidOperationException(LocalizedText.Format(
                Strings.Error_UsvfsMapFailedFormat,
                operation.SourcePath,
                operation.DestinationPath));
        }

        progress?.Report(LocalizedText.Format(Strings.Progress_UsvfsMappedFormat, operation.SourceName));
    }

    internal static void ValidateLaunchRequest(UsvfsProcessLaunchRequest launchRequest)
    {
        if (string.IsNullOrWhiteSpace(launchRequest.ExecutablePath))
        {
            throw new ArgumentException(Strings.Error_LaunchExecutablePathEmpty, nameof(launchRequest));
        }

        if (string.IsNullOrWhiteSpace(launchRequest.WorkingDirectory))
        {
            throw new ArgumentException(Strings.Error_LaunchWorkingDirectoryEmpty, nameof(launchRequest));
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed class UsvfsRuntimeSession : IUsvfsRuntimeSession
{
    private readonly IUsvfsNativeApi _nativeApi;
    private readonly IntPtr _parameters;
    private readonly UsvfsSessionReservation _reservation;
    private readonly UsvfsLogCollector? _logCollector;
    private readonly object _sync = new();
    private Task<int>? _exitCodeTask;
    private bool _disposed;

    internal UsvfsRuntimeSession(
        IUsvfsNativeApi nativeApi,
        IntPtr parameters,
        UsvfsSessionReservation reservation,
        UsvfsLogCollector? logCollector)
    {
        _nativeApi = nativeApi;
        _parameters = parameters;
        _reservation = reservation;
        _logCollector = logCollector;
    }

    public Process StartProcess(
        UsvfsProcessLaunchRequest launchRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UsvfsRuntime.ValidateLaunchRequest(launchRequest);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exitCodeTask is not null)
            {
                throw new InvalidOperationException(Strings.Error_UsvfsSessionAlreadyStarted);
            }

            var commandLine = UsvfsRuntime.BuildCommandLine(launchRequest.ExecutablePath, launchRequest.Arguments);
            progress?.Report(LocalizedText.Format(Strings.Progress_UsvfsStartingFormat, launchRequest.ExecutablePath));
            var handle = _nativeApi.CreateProcessHookedAsync(
                    launchRequest.ExecutablePath,
                    commandLine,
                    launchRequest.WorkingDirectory,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();

            _exitCodeTask = handle.ExitCodeTask;
            return Process.GetProcessById(handle.ProcessId);
        }
    }

    public async Task<int> GetExitCodeAsync(CancellationToken cancellationToken = default)
    {
        Task<int>? task;
        lock (_sync)
        {
            task = _exitCodeTask;
        }

        if (task is null)
        {
            throw new InvalidOperationException(Strings.Error_UsvfsSessionNotStarted);
        }

        return await task.WaitAsync(cancellationToken);
    }

    public IReadOnlyList<int> GetActiveProcessIds()
    {
        lock (_sync)
        {
            return _disposed ? [] : _nativeApi.GetVfsProcessIds();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        try
        {
            _logCollector?.Dispose();
        }
        finally
        {
            try
            {
                _nativeApi.DisconnectVfs();
            }
            finally
            {
                try
                {
                    _nativeApi.FreeParameters(_parameters);
                }
                finally
                {
                    _reservation.Dispose();
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
