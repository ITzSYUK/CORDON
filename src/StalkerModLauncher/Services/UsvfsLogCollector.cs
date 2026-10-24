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
    private long _logFileBytes;
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
            var pollDelayMilliseconds = 0;

            do
            {
                var messagesRead = Drain(ref writer);
                pollDelayMilliseconds = messagesRead > 0 ? 0 : Math.Min(100, pollDelayMilliseconds + 25);
            }
            while (!_stop.Wait(pollDelayMilliseconds));

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

    private int Drain(ref StreamWriter writer)
    {
        const int maxMessagesPerPass = 4096;
        var messagesRead = 0;
        for (; messagesRead < maxMessagesPerPass; messagesRead++)
        {
            if (!_nativeApi.TryGetLogMessage(out var message))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                var line = message.TrimEnd();
                var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_logFileBytes + lineBytes > MaxLogFileBytes)
                {
                    writer.Dispose();
                    RotateLogFile(_logPath);
                    writer = OpenWriter();
                }

                writer.WriteLine(line);
                _logFileBytes += lineBytes;
            }
        }

        writer.Flush();
        return messagesRead;
    }

    private StreamWriter OpenWriter()
    {
        var writer = new StreamWriter(
            new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logFileBytes = writer.BaseStream.Length;
        return writer;
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
