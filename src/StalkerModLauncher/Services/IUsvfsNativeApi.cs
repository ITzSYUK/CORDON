namespace StalkerModLauncher.Services;

[Flags]
public enum UsvfsLinkOptions : uint
{
    None = 0,
    FailIfExists = 0x00000001,
    MonitorChanges = 0x00000002,
    CreateTarget = 0x00000004,
    Recursive = 0x00000008,
    FailIfSkipped = 0x00000010
}

public enum UsvfsLogLevel : byte
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public enum UsvfsCrashDumpType : byte
{
    None = 0,
    Mini = 1,
    Data = 2,
    Full = 3
}

public interface IUsvfsNativeApi
{
    IntPtr CreateParameters();
    void FreeParameters(IntPtr parameters);
    void SetInstanceName(IntPtr parameters, string name);
    void SetDebugMode(IntPtr parameters, bool debugMode);
    void SetLogLevel(IntPtr parameters, UsvfsLogLevel level);
    void SetCrashDumpType(IntPtr parameters, UsvfsCrashDumpType type);
    void SetCrashDumpPath(IntPtr parameters, string path);
    void InitLogging(bool toLocal);
    bool CreateVfs(IntPtr parameters);
    void DisconnectVfs();
    void ClearVirtualMappings();
    IReadOnlyList<int> GetVfsProcessIds();
    bool TryGetLogMessage(out string message);
    bool LinkDirectoryStatic(string sourcePath, string destinationPath, UsvfsLinkOptions flags);
    bool LinkFile(string sourcePath, string destinationPath, UsvfsLinkOptions flags);

    Task<UsvfsProcessHandle> CreateProcessHookedAsync(
        string executablePath,
        string commandLine,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record UsvfsProcessHandle(
    System.Diagnostics.Process Process,
    Task<int> ExitCodeTask);
