using System.Text;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed class UsvfsLogCollector : IDisposable
{
    private const long MaxLogFileBytes = 2L * 1024 * 1024;
    private readonly IUsvfsNativeApi _nativeApi;
    private readonly string _logPath;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private int _disposed;

    public UsvfsLogCollector(IUsvfsNativeApi nativeApi, string logPath)
    {
        _nativeApi = nativeApi;
        _logPath = Path.GetFullPath(logPath);
        PrepareLogFile(_logPath);
        _thread = new Thread(Collect)
        {
            IsBackground = true,
            Name = Strings.Layer_UsvfsLogCollector
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    private void Collect()
    {
        StreamWriter? writer = null;
        try
        {
            writer = OpenWriter();

            do
            {
                Drain(ref writer);
            }
            while (!_stop.Wait(TimeSpan.FromMilliseconds(25)));

            Drain(ref writer);
        }
        catch
        {
            // USVFS diagnostics are best-effort and must never terminate a game session.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void Drain(ref StreamWriter writer)
    {
        const int maxMessagesPerPass = 4096;
        for (var index = 0; index < maxMessagesPerPass; index++)
        {
            if (!_nativeApi.TryGetLogMessage(out var message))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                var line = message.TrimEnd();
                var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (writer.BaseStream.Length + lineBytes > MaxLogFileBytes)
                {
                    writer.Dispose();
                    RotateLogFile(_logPath);
                    writer = OpenWriter();
                }

                writer.WriteLine(line);
            }
        }
    }

    private StreamWriter OpenWriter()
    {
        return new StreamWriter(
            new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private static void PrepareLogFile(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogFileBytes)
        {
            return;
        }

        RotateLogFile(logPath);
    }

    private static void RotateLogFile(string logPath)
    {
        var oldLogPath = Path.Combine(
            Path.GetDirectoryName(logPath)!,
            Path.GetFileNameWithoutExtension(logPath) + ".old" + Path.GetExtension(logPath));
        File.Move(logPath, oldLogPath, overwrite: true);
    }
}
