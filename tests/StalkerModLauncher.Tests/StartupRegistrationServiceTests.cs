using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void ConfigureWritesQuotedMinimizedCommandToWindowsRunStore()
    {
        var store = new CapturingStartupRegistrationStore();
        var service = new StartupRegistrationService(store, () => @"C:\Launcher Folder\Launcher.exe");

        service.Configure(enabled: true, startMinimizedToTray: true);

        Assert.Equal("StalkerModLauncher", store.SetName);
        Assert.Equal("\"C:\\Launcher Folder\\Launcher.exe\" --minimized", store.Command);
        Assert.Null(store.RemovedName);
    }

    [Fact]
    public void ConfigureDisabledRemovesWindowsRunValueWithoutResolvingExecutable()
    {
        var store = new CapturingStartupRegistrationStore();
        var service = new StartupRegistrationService(
            store,
            () => throw new InvalidOperationException("Must not be called"));

        service.Configure(enabled: false, startMinimizedToTray: true);

        Assert.Equal("StalkerModLauncher", store.RemovedName);
        Assert.Null(store.Command);
    }

    [Fact]
    public void ConfigureVisibleStartupOmitsMinimizedArgument()
    {
        var store = new CapturingStartupRegistrationStore();
        var service = new StartupRegistrationService(store, () => @"C:\Launcher.exe");

        service.Configure(enabled: true, startMinimizedToTray: false);

        Assert.Equal("\"C:\\Launcher.exe\"", store.Command);
    }

    [Fact]
    public void StandaloneProcessWinsOverRegularExecutableInSameDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CordonStartupTests-{Guid.NewGuid():N}");
        var standalone = Path.Combine(directory, "CORDON-Standalone.exe");

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "CORDON.exe"), string.Empty);

        try
        {
            Assert.Equal(standalone, StartupRegistrationService.ResolveExecutablePath(standalone, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturingStartupRegistrationStore : IStartupRegistrationStore
    {
        public string? SetName { get; private set; }
        public string? Command { get; private set; }
        public string? RemovedName { get; private set; }

        public void SetCommand(string name, string command)
        {
            SetName = name;
            Command = command;
        }

        public void Remove(string name) => RemovedName = name;
    }
}
