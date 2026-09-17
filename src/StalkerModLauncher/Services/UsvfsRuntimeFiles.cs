using System.Diagnostics;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed record UsvfsRuntimeFileStatus(
    string Directory,
    string ControllerDllPath,
    string X64ProxyPath,
    string X86DllPath,
    string X86ProxyPath,
    string X86HostPath)
{
    public IReadOnlyList<string> MissingFileNames =>
        RuntimeFiles
            .Where(file => !File.Exists(file.Path))
            .Select(file => file.Name)
            .ToArray();

    public IReadOnlyList<string> ValidationErrors
    {
        get
        {
            var errors = new List<string>();
            foreach (var file in RuntimeFiles.Where(file => File.Exists(file.Path)))
            {
                var actualArchitecture = WindowsExecutableArchitectureDetector.Detect(file.Path);
                if (actualArchitecture != file.Architecture)
                {
                    errors.Add(LocalizedText.Format(Strings.Usvfs_ArchitectureMismatchFormat, file.Name, FormatArchitecture(file.Architecture), FormatArchitecture(actualArchitecture)));
                }
            }

            var versionedFiles = RuntimeFiles
                .Where(file => file.IsUpstreamComponent && File.Exists(file.Path))
                .Select(file => new
                {
                    file.Name,
                    Version = FileVersionInfo.GetVersionInfo(file.Path).FileVersion
                })
                .ToArray();
            foreach (var file in versionedFiles.Where(file => string.IsNullOrWhiteSpace(file.Version)))
            {
                errors.Add(LocalizedText.Format(Strings.Usvfs_FileVersionMissingFormat, file.Name));
            }

            var versions = versionedFiles
                .Select(file => file.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (versions.Length > 1)
            {
                errors.Add(LocalizedText.Format(Strings.Usvfs_VersionMismatchFormat, string.Join(", ", versions)));
            }

            return errors;
        }
    }

    public string? RuntimeVersion
    {
        get
        {
            if (!IsReady)
            {
                return null;
            }

            return FileVersionInfo.GetVersionInfo(ControllerDllPath).FileVersion;
        }
    }

    public bool IsX64Ready => IsReady;
    public bool IsX86Ready => IsReady;
    public bool IsReady => MissingFileNames.Count == 0 && ValidationErrors.Count == 0;

    public bool IsReadyFor(WindowsExecutableArchitecture architecture) =>
        architecture is WindowsExecutableArchitecture.X86 or WindowsExecutableArchitecture.X64 && IsReady;

    public string MissingFilesMessage(WindowsExecutableArchitecture architecture = WindowsExecutableArchitecture.Unknown)
    {
        if (IsReadyFor(architecture))
        {
            return string.Empty;
        }

        var problems = MissingFileNames
            .Select(name => LocalizedText.Format(Strings.Usvfs_FileMissingFormat, name))
            .Concat(ValidationErrors)
            .ToArray();
        var target = architecture switch
        {
            WindowsExecutableArchitecture.X86 => Strings.Usvfs_TargetX86,
            WindowsExecutableArchitecture.X64 => Strings.Usvfs_TargetX64,
            _ => Strings.Usvfs_TargetSelected
        };
        return LocalizedText.Format(Strings.Usvfs_RuntimeInvalidFormat, target, string.Join(" ", problems), Directory);
    }

    private IReadOnlyList<RuntimeFile> RuntimeFiles =>
    [
        new(UsvfsRuntimeFiles.ControllerDllFileName, ControllerDllPath, WindowsExecutableArchitecture.X64, true),
        new(UsvfsRuntimeFiles.X64ProxyFileName, X64ProxyPath, WindowsExecutableArchitecture.X64, true),
        new(UsvfsRuntimeFiles.X86DllFileName, X86DllPath, WindowsExecutableArchitecture.X86, true),
        new(UsvfsRuntimeFiles.X86ProxyFileName, X86ProxyPath, WindowsExecutableArchitecture.X86, true),
        new(UsvfsRuntimeFiles.X86HostFileName, X86HostPath, WindowsExecutableArchitecture.X86, false)
    ];

    private static string FormatArchitecture(WindowsExecutableArchitecture architecture) =>
        architecture switch
        {
            WindowsExecutableArchitecture.X86 => "x86",
            WindowsExecutableArchitecture.X64 => "x64",
            _ => Strings.Usvfs_ArchitectureUnknown
        };

    private sealed record RuntimeFile(
        string Name,
        string Path,
        WindowsExecutableArchitecture Architecture,
        bool IsUpstreamComponent);
}

public static class UsvfsRuntimeFiles
{
    public const string ControllerDllFileName = "usvfs_x64.dll";
    public const string X64ProxyFileName = "usvfs_proxy_x64.exe";
    public const string X86DllFileName = "usvfs_x86.dll";
    public const string X86ProxyFileName = "usvfs_proxy_x86.exe";
    public const string X86HostFileName = "StalkerModLauncher.UsvfsX86Host.exe";

    public const string DllFileName = ControllerDllFileName;
    public const string ProxyFileName = X64ProxyFileName;

    public static UsvfsRuntimeFileStatus Check(string? directory = null)
    {
        var root = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        return new UsvfsRuntimeFileStatus(
            root,
            Path.Combine(root, ControllerDllFileName),
            Path.Combine(root, X64ProxyFileName),
            Path.Combine(root, X86DllFileName),
            Path.Combine(root, X86ProxyFileName),
            Path.Combine(root, X86HostFileName));
    }
}
