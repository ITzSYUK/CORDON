using System.Diagnostics;
using System.Text;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class X86UsvfsHostRuntime(string? runtimeDirectory = null) : IUsvfsRuntime
{
    private readonly string _runtimeDirectory = Path.GetFullPath(runtimeDirectory ?? AppContext.BaseDirectory);

    public IUsvfsRuntimeSession CreateSession(
        UsvfsMappingPlan mappingPlan,
        UsvfsRuntimeOptions options,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mappingPlan);
        ArgumentNullException.ThrowIfNull(options);
        return new Session(_runtimeDirectory, mappingPlan, options, UsvfsSessionReservation.Acquire());
    }

    public async Task<UsvfsProcessLaunchResult> RunAsync(
        UsvfsMappingPlan mappingPlan,
        UsvfsProcessLaunchRequest launchRequest,
        UsvfsRuntimeOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var session = CreateSession(mappingPlan, options, progress);
        using var process = session.StartProcess(launchRequest, progress, cancellationToken);
        var processId = process.Id;
        var exitCode = await session.GetExitCodeAsync(cancellationToken);
        return new UsvfsProcessLaunchResult(exitCode, processId);
    }

    internal static IReadOnlyList<int> ReadProcessIds(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(line => int.TryParse(line, out var processId) ? processId : 0)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Session : IUsvfsRuntimeSession
    {
        private const uint ConfigMagic = 0x32534656;
        private readonly string _runtimeDirectory;
        private readonly UsvfsMappingPlan _mappingPlan;
        private readonly UsvfsRuntimeOptions _options;
        private readonly UsvfsSessionReservation _reservation;
        private Process? _hostProcess;
        private string? _configurationPath;
        private string? _processListPath;
        private bool _disposed;

        public Session(
            string runtimeDirectory,
            UsvfsMappingPlan mappingPlan,
            UsvfsRuntimeOptions options,
            UsvfsSessionReservation reservation)
        {
            _runtimeDirectory = runtimeDirectory;
            _mappingPlan = mappingPlan;
            _options = options;
            _reservation = reservation;
        }

        public Process StartProcess(
            UsvfsProcessLaunchRequest launchRequest,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hostProcess is not null)
            {
                throw new InvalidOperationException(Strings.Error_UsvfsX86SessionAlreadyStarted);
            }

            UsvfsRuntime.ValidateLaunchRequest(launchRequest);
            var hostPath = Path.Combine(_runtimeDirectory, UsvfsRuntimeFiles.X86HostFileName);
            if (!File.Exists(hostPath))
            {
                throw new FileNotFoundException(Strings.Error_UsvfsX86HostMissing, hostPath);
            }

            _configurationPath = Path.Combine(
                AppPaths.Current.TempDirectory,
                $"stalker-usvfs-x86-{Guid.NewGuid():N}.bin");
            _processListPath = _configurationPath + ".pids";
            Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
            WriteConfiguration(_configurationPath, _mappingPlan, launchRequest, _options);
            progress?.Report(LocalizedText.Format(Strings.Progress_UsvfsX86StartingFormat, launchRequest.ExecutablePath));
            _hostProcess = Process.Start(new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = Quote(_configurationPath),
                WorkingDirectory = _runtimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException(Strings.Error_UsvfsX86HostStartFailed);
            return _hostProcess;
        }

        public async Task<int> GetExitCodeAsync(CancellationToken cancellationToken = default)
        {
            var process = _hostProcess ??
                          throw new InvalidOperationException(Strings.Error_UsvfsX86SessionNotStarted);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }

        public IReadOnlyList<int> GetActiveProcessIds()
        {
            var processIds = new HashSet<int>(ReadProcessIds(_processListPath));
            try
            {
                if (_hostProcess is { HasExited: false } process)
                {
                    processIds.Add(process.Id);
                }
            }
            catch (InvalidOperationException)
            {
            }

            return processIds.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            // The Process returned by StartProcess belongs to the launch/session tracker.
            // Disposing it here races with GameSessionTracker reading ExitCode.
            _hostProcess = null;
            foreach (var path in new[] { _configurationPath, _processListPath })
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _reservation.Dispose();
            return ValueTask.CompletedTask;
        }

        private static void WriteConfiguration(
            string path,
            UsvfsMappingPlan mappingPlan,
            UsvfsProcessLaunchRequest request,
            UsvfsRuntimeOptions options)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(ConfigMagic);
            WriteUtf8(writer, options.InstanceName);
            WriteUtf8(writer, request.ExecutablePath);
            WriteUtf8(writer, request.Arguments ?? string.Empty);
            WriteUtf8(writer, request.WorkingDirectory);
            WriteUtf8(writer, options.DiagnosticLogPath ?? string.Empty);
            var operations = mappingPlan.Operations
                .OrderBy(operation => operation.Order)
                .ToArray();
            writer.Write((uint)operations.Length);
            foreach (var operation in operations)
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

                writer.Write((uint)operation.Kind);
                writer.Write((uint)flags);
                WriteUtf8(writer, operation.SourcePath);
                WriteUtf8(writer, operation.DestinationPath);
            }
        }

        private static void WriteUtf8(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
