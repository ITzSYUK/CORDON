using Microsoft.Win32;

namespace StalkerModLauncher.Services;

public interface IStartupRegistrationService
{
    void Configure(bool enabled, bool startMinimizedToTray);
}

public interface IStartupRegistrationStore
{
    void SetCommand(string name, string command);
    void Remove(string name);
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string ValueName = "StalkerModLauncher";
    private readonly IStartupRegistrationStore _store;
    private readonly Func<string> _resolveExecutablePath;

    public StartupRegistrationService(
        IStartupRegistrationStore? store = null,
        Func<string>? resolveExecutablePath = null)
    {
        _store = store ?? new WindowsRunStartupRegistrationStore();
        _resolveExecutablePath = resolveExecutablePath ?? ResolveExecutablePath;
    }

    public void Configure(bool enabled, bool startMinimizedToTray)
    {
        if (!enabled)
        {
            _store.Remove(ValueName);
            return;
        }

        var minimizedArgument = startMinimizedToTray ? " --minimized" : string.Empty;
        _store.SetCommand(ValueName, $"\"{_resolveExecutablePath()}\"{minimizedArgument}");
    }

    private static string ResolveExecutablePath() =>
        ResolveExecutablePath(Environment.ProcessPath, AppContext.BaseDirectory);

    internal static string ResolveExecutablePath(string? processPath, string baseDirectory)
    {
        if (AppPaths.IsStandaloneExecutable(processPath))
        {
            return Path.GetFullPath(processPath!);
        }

        var packagedExecutable = Path.Combine(baseDirectory, "CORDON.exe");
        if (File.Exists(packagedExecutable))
        {
            return packagedExecutable;
        }

        return processPath
            ?? throw new InvalidOperationException("Не удалось определить путь к лаунчеру.");
    }
}

public sealed class WindowsRunStartupRegistrationStore : IStartupRegistrationStore
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetCommand(string name, string command)
    {
        using var key = OpenWritableKey();
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = OpenWritableKey();
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static RegistryKey OpenWritableKey() =>
        Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
        ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");
}
