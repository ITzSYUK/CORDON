using System.Collections.Concurrent;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class UsvfsRuntimeTests
{
    [Fact]
    public async Task RunAsyncAppliesMappingsInPriorityOrderAndRunsHookedProcess()
    {
        var native = new FakeUsvfsNativeApi();
        var runtime = new UsvfsRuntime(native);
        var plan = new UsvfsMappingPlan(
            @"C:\game",
            @"C:\workspace\userdata\overwrite",
            [
                new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    @"C:\mod",
                    @"C:\game",
                    "patch",
                    2),
                new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    @"C:\game",
                    @"C:\game",
                    "base",
                    1),
                new UsvfsMappingOperation(
                    UsvfsMappingKind.File,
                    @"C:\workspace\userdata\writable-game-files\localization.ltx",
                    @"C:\game\gamedata\configs\localization.ltx",
                    "profile writable files",
                    3),
                new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    @"C:\workspace\userdata\overwrite",
                    @"C:\game",
                    "profile overwrite",
                    4,
                    MonitorChanges: true,
                    CreateTarget: true)
            ]);

        var result = await runtime.RunAsync(
            plan,
            new UsvfsProcessLaunchRequest(@"C:\game\bin\xrEngine.exe", "-nointro", @"C:\game"),
            new UsvfsRuntimeOptions("test-instance"));

        Assert.Equal(Environment.ProcessId, result.ProcessId);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("create-vfs", native.Calls);
        Assert.True(native.LogToConsole);
        Assert.Contains("clear-mappings", native.Calls);
        Assert.Contains("disconnect", native.Calls);
        Assert.Contains("free-parameters", native.Calls);
        Assert.Equal(
            ["base", "patch", "profile writable files", "profile overwrite"],
            native.Mappings.Select(mapping => mapping.SourceName).ToArray());
        Assert.Equal(UsvfsLinkOptions.Recursive, native.Mappings[0].Flags);
        Assert.Equal(
            UsvfsLinkOptions.Recursive | UsvfsLinkOptions.MonitorChanges | UsvfsLinkOptions.CreateTarget,
            native.Mappings[3].Flags);
        Assert.Equal("\"C:\\game\\bin\\xrEngine.exe\" -nointro", native.CommandLine);
        Assert.Equal(@"C:\game", native.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsyncDisconnectsAndFreesParametersWhenMappingFails()
    {
        var native = new FakeUsvfsNativeApi { FailNextMapping = true };
        var runtime = new UsvfsRuntime(native);
        var plan = new UsvfsMappingPlan(
            @"C:\game",
            @"C:\overwrite",
            [
                new UsvfsMappingOperation(
                    UsvfsMappingKind.DirectoryStatic,
                    @"C:\mod",
                    @"C:\game",
                    "mod",
                    1)
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync(
            plan,
            new UsvfsProcessLaunchRequest(@"C:\game\xrEngine.exe", null, @"C:\game"),
            new UsvfsRuntimeOptions("test-instance")));

        Assert.Contains("disconnect", native.Calls);
        Assert.Contains("free-parameters", native.Calls);
    }

    [Fact]
    public async Task CreateSessionRejectsConcurrentSessionAndAllowsNextAfterDispose()
    {
        var runtime = new UsvfsRuntime(new FakeUsvfsNativeApi());
        var plan = new UsvfsMappingPlan(@"C:\game", @"C:\overwrite", []);
        var options = new UsvfsRuntimeOptions("test-instance");
        var first = runtime.CreateSession(plan, options);

        var error = Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(plan, options));
        Assert.Contains("уже запущен", error.Message);

        await first.DisposeAsync();
        await using var next = runtime.CreateSession(plan, options);
    }

    [Fact]
    public async Task CreateSessionDrainsDiagnosticMessagesToProfileLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherUsvfsLogTests", Guid.NewGuid().ToString("N"));
        try
        {
            var native = new FakeUsvfsNativeApi();
            native.LogMessages.Enqueue("hooked file: gamedata\\config\\system.ltx");
            var runtime = new UsvfsRuntime(native);
            var logPath = Path.Combine(root, "userdata", "logs", "usvfs.log");
            var plan = new UsvfsMappingPlan(@"C:\virtual", @"C:\overwrite", []);

            await using (runtime.CreateSession(
                             plan,
                             new UsvfsRuntimeOptions(
                                 "test-instance",
                                 LogToConsole: false,
                                 DiagnosticLogPath: logPath)))
            {
            }

            Assert.False(native.LogToConsole);
            Assert.Contains("hooked file", await File.ReadAllTextAsync(logPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void X86HostProcessListIgnoresInvalidEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stalker-usvfs-pids-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllLines(path, ["123", "invalid", "0", "456", "123"]);

            Assert.Equal([123, 456], X86UsvfsHostRuntime.ReadProcessIds(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeUsvfsNativeApi : IUsvfsNativeApi
    {
        private static readonly IntPtr Parameters = new(42);

        public List<string> Calls { get; } = [];
        public List<MappingCall> Mappings { get; } = [];
        public bool FailNextMapping { get; init; }
        public string? CommandLine { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public bool? LogToConsole { get; private set; }
        public ConcurrentQueue<string> LogMessages { get; } = new();

        public IntPtr CreateParameters()
        {
            Calls.Add("create-parameters");
            return Parameters;
        }

        public void FreeParameters(IntPtr parameters)
        {
            Assert.Equal(Parameters, parameters);
            Calls.Add("free-parameters");
        }

        public void SetInstanceName(IntPtr parameters, string name)
        {
            Assert.Equal("test-instance", name);
            Calls.Add("set-instance");
        }

        public void SetDebugMode(IntPtr parameters, bool debugMode) => Calls.Add("set-debug");
        public void SetLogLevel(IntPtr parameters, UsvfsLogLevel level) => Calls.Add("set-log-level");
        public void SetCrashDumpType(IntPtr parameters, UsvfsCrashDumpType type) => Calls.Add("set-dump-type");
        public void SetCrashDumpPath(IntPtr parameters, string path) => Calls.Add("set-dump-path");
        public void InitLogging(bool toLocal)
        {
            LogToConsole = toLocal;
            Calls.Add("init-logging");
        }

        public bool CreateVfs(IntPtr parameters)
        {
            Calls.Add("create-vfs");
            return true;
        }

        public void DisconnectVfs() => Calls.Add("disconnect");
        public void ClearVirtualMappings() => Calls.Add("clear-mappings");
        public IReadOnlyList<int> GetVfsProcessIds() => [];
        public bool TryGetLogMessage(out string message) => LogMessages.TryDequeue(out message!);

        public bool LinkDirectoryStatic(string sourcePath, string destinationPath, UsvfsLinkOptions flags)
        {
            Mappings.Add(new MappingCall("dir", sourcePath, destinationPath, InferSourceName(sourcePath), flags));
            return !FailNextMapping;
        }

        public bool LinkFile(string sourcePath, string destinationPath, UsvfsLinkOptions flags)
        {
            Mappings.Add(new MappingCall("file", sourcePath, destinationPath, InferSourceName(sourcePath), flags));
            return !FailNextMapping;
        }

        public Task<UsvfsProcessHandle> CreateProcessHookedAsync(
            string executablePath,
            string commandLine,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            CommandLine = commandLine;
            WorkingDirectory = workingDirectory;
            Calls.Add("create-process-hooked");
            return Task.FromResult(new UsvfsProcessHandle(Environment.ProcessId, Task.FromResult(0)));
        }

        private static string InferSourceName(string sourcePath)
        {
            if (sourcePath.EndsWith("game", StringComparison.OrdinalIgnoreCase))
            {
                return "base";
            }

            if (sourcePath.EndsWith("mod", StringComparison.OrdinalIgnoreCase))
            {
                return "patch";
            }

            if (sourcePath.EndsWith("localization.ltx", StringComparison.OrdinalIgnoreCase))
            {
                return "profile writable files";
            }

            return "profile overwrite";
        }
    }

    private sealed record MappingCall(
        string Kind,
        string SourcePath,
        string DestinationPath,
        string SourceName,
        UsvfsLinkOptions Flags);
}
