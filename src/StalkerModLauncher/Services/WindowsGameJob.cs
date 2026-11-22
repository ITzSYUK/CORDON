using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace StalkerModLauncher.Services;

internal sealed class WindowsGameJob(SafeWaitHandle handle) : IDisposable
{
    private const int ErrorAlreadyExists = 183;
    private const int ErrorMoreData = 234;
    private const uint CreateSuspended = 0x00000004;
    private const uint JobAssignProcessAndQuery = 0x0005;
    private const int BasicAccountingInformation = 1;
    private const int BasicProcessIdList = 3;

    public static WindowsGameJob Create(string? name = null)
    {
        if (name is null)
            return FromHandle(CreateJobObjectW(IntPtr.Zero, null));

        // The UAC prompt may use a different administrator account.
        using var identity = WindowsIdentity.GetCurrent();
        var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;{identity.User})(A;;GA;;;BA)(A;;GA;;;SY)");
        var buffer = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(buffer, 0);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject()
            };
            var job = CreateJobObjectW(ref attributes, name);
            if (Marshal.GetLastWin32Error() == ErrorAlreadyExists)
            {
                job.Dispose();
                throw new Win32Exception(ErrorAlreadyExists);
            }

            return FromHandle(job);
        }
        finally
        {
            pinned.Free();
        }
    }

    public static WindowsGameJob Open(string name) =>
        FromHandle(OpenJobObjectW(JobAssignProcessAndQuery, false, name));

    private static WindowsGameJob FromHandle(SafeWaitHandle job)
    {
        if (!job.IsInvalid) return new WindowsGameJob(job);
        var error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error);
    }

    public Process Start(string executable, string arguments, string workingDirectory)
    {
        var commandLine = Marshal.StringToHGlobalUni($"\"{executable}\" {arguments}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        ProcessInformation created;
        try
        {
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended, IntPtr.Zero, workingDirectory, ref startup, out created))
                throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }

        using var processHandle = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(created.Thread, ownsHandle: true);
        Process? process = null;
        try
        {
            // A wrapper can create its child and exit before an ordinary Process.Start returns.
            if (!AssignProcessToJobObject(handle, processHandle))
                throw new Win32Exception();

            process = Process.GetProcessById(created.ProcessId);
            _ = process.SafeHandle;
            if (ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception();

            return process;
        }
        catch
        {
            TerminateProcess(processHandle, 1);
            process?.Dispose();
            throw;
        }
    }

    public ProfileLaunchHandle Track(Process process) =>
        new(process, CompleteAndDisposeAsync(process), GetActiveProcessIds, TryTerminate, includeRootProcess: false);

    public async Task<int> WaitForCompletionAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        var exitCode = process.ExitCode;
        while (GetActiveProcessCount() != 0)
            await Task.Delay(100).ConfigureAwait(false);

        return exitCode;
    }

    private async Task<int> CompleteAndDisposeAsync(Process process)
    {
        using (this) return await WaitForCompletionAsync(process).ConfigureAwait(false);
    }

    private uint GetActiveProcessCount()
    {
        if (!QueryInformationJobObject(handle, BasicAccountingInformation, out var info,
                Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
            throw new Win32Exception();

        return info.ActiveProcesses;
    }

    private int[] GetActiveProcessIds()
    {
        const int headerSize = 2 * sizeof(int);
        for (var capacity = 16; ; capacity = checked(capacity * 2))
        {
            var size = checked(headerSize + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(handle, BasicProcessIdList, buffer, size, IntPtr.Zero))
                {
                    var ids = new int[Marshal.ReadInt32(buffer, sizeof(int))];
                    for (var index = 0; index < ids.Length; index++)
                        ids[index] = Marshal.ReadIntPtr(buffer, headerSize + index * IntPtr.Size).ToInt32();

                    return ids;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorMoreData) throw new Win32Exception(error);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private bool TryTerminate()
    {
        try
        {
            return GetActiveProcessCount() > 0 && TerminateJobObject(handle, 1);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateJobObjectW(ref SecurityAttributes attributes, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenJobObjectW(uint access, bool inheritHandle, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string application, IntPtr commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeWaitHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeWaitHandle job, int informationClass, out JobAccounting result, int size, IntPtr returnedSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeWaitHandle job, int informationClass, IntPtr result, int size, IntPtr returnedSize);
}
