using System.Diagnostics;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

if (args.Length > 0 && args[0] == "--child")
{
    return RunChild(args);
}

if (args.Length > 0 && args[0] == "--launcher")
{
    return RunLauncher(args);
}

if (args.Length > 0 && args[0] == "--read")
{
    return RunRead(args);
}

return await RunHostAsync(args);

static async Task<int> RunHostAsync(string[] args)
{
    var arguments = args.ToList();
    var iterations = 1;
    var iterationsIndex = arguments.IndexOf("--iterations");
    if (iterationsIndex >= 0)
    {
        if (iterationsIndex + 1 >= arguments.Count ||
            !int.TryParse(arguments[iterationsIndex + 1], out iterations) ||
            iterations is < 1 or > 100)
        {
            Console.Error.WriteLine("--iterations must be between 1 and 100.");
            return 10;
        }

        arguments.RemoveRange(iterationsIndex, 2);
    }

    args = arguments.ToArray();
    if (args.Length is < 1 or > 3)
    {
        Console.Error.WriteLine(
            "Usage: StalkerUsvfsManagedPoc <built-usvfs-source-root> [x86-child-exe] [x86-launcher-exe] [--iterations N]");
        return 10;
    }

    var usvfsRoot = Path.GetFullPath(args[0]);
    CopyUsvfsRuntime(usvfsRoot, AppContext.BaseDirectory);
    CopyRequired(
        Path.Combine(
            Directory.GetCurrentDirectory(),
            "native",
            "StalkerModLauncher.UsvfsX86Host",
            "build32",
            UsvfsRuntimeFiles.X86HostFileName),
        Path.Combine(AppContext.BaseDirectory, UsvfsRuntimeFiles.X86HostFileName));
    var runtimeFiles = UsvfsRuntimeFiles.Check(AppContext.BaseDirectory);
    if (!runtimeFiles.IsReady)
    {
        throw new InvalidOperationException(
            runtimeFiles.MissingFilesMessage(WindowsExecutableArchitecture.Unknown));
    }

    var root = Path.Combine(Path.GetTempPath(), $"stalker-usvfs-managed-poc-{Environment.ProcessId}");
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    var baseRoot = Path.Combine(root, "base");
    var modRoot = Path.Combine(root, "mod");
    var virtualRoot = Path.Combine(root, "virtual-root");
    var resultPath = Path.Combine(root, "result.txt");
    var profileFile = Path.Combine(root, "profile-files", "fsgame.ltx");

    WriteText(Path.Combine(virtualRoot, "physical-bootstrap.txt"), "bootstrap");
    WriteText(Path.Combine(baseRoot, "shared.txt"), "base");
    WriteText(Path.Combine(baseRoot, "base-only.txt"), "base");
    WriteText(Path.Combine(baseRoot, "gamedata", "config", "system.ltx"), "base-system");
    WriteText(Path.Combine(modRoot, "mod-only.txt"), "mod");
    WriteText(Path.Combine(modRoot, "gamedata", "config", "system.ltx"), "mod-system");
    WriteText(profileFile, "profile-fsgame");

    var plan = new UsvfsMappingPlan(
        virtualRoot,
        Path.Combine(root, "overwrite"),
        [
            new UsvfsMappingOperation(UsvfsMappingKind.DirectoryStatic, baseRoot, virtualRoot, "base", 1),
            new UsvfsMappingOperation(UsvfsMappingKind.DirectoryStatic, modRoot, virtualRoot, "mod", 2),
            new UsvfsMappingOperation(
                UsvfsMappingKind.File,
                profileFile,
                Path.Combine(virtualRoot, "fsgame.ltx"),
                "profile file",
                3)
        ]);

    var childExecutable = args.Length >= 2
        ? Path.GetFullPath(args[^1])
        : Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
    if (!File.Exists(childExecutable))
    {
        throw new FileNotFoundException("USVFS PoC child executable was not found.", childExecutable);
    }

    var childArguments = args.Length == 3
        ? $"{Quote(Path.GetFullPath(args[1]))} {Quote(virtualRoot)} {Quote(resultPath)}"
        : args.Length == 2
            ? $"{Quote(virtualRoot)} {Quote(resultPath)}"
        : $"--launcher {Quote(virtualRoot)} {Quote(resultPath)}";
    IUsvfsRuntime runtime = args.Length >= 2
        ? new X86UsvfsHostRuntime(AppContext.BaseDirectory)
        : new UsvfsRuntime(new OfficialUsvfsNativeApi());
    if (args.Length == 1)
    {
        await VerifyWritableDirectoryMappingAsync(runtime, root, childExecutable);
    }

    for (var iteration = 1; iteration <= iterations; iteration++)
    {
        var expectedModValue = $"mod-{iteration}";
        WriteText(Path.Combine(modRoot, "shared.txt"), expectedModValue);
        var dynamicPath = Path.Combine(modRoot, "dynamic.txt");
        var expectedDynamicValue = (iteration % 3) switch
        {
            1 => $"added-{iteration}",
            2 => $"changed-{iteration}",
            _ => "<missing>"
        };
        if (expectedDynamicValue == "<missing>")
        {
            File.Delete(dynamicPath);
        }
        else
        {
            WriteText(dynamicPath, expectedDynamicValue);
        }

        File.Delete(resultPath);

        var result = await runtime.RunAsync(
            plan,
            new UsvfsProcessLaunchRequest(
                childExecutable,
                childArguments,
                AppContext.BaseDirectory),
            new UsvfsRuntimeOptions(
                $"stalker_launcher_managed_usvfs_poc_{iteration}",
                LogToConsole: false,
                DiagnosticLogPath: Path.Combine(root, "logs", "usvfs.log")));

        var output = File.Exists(resultPath) ? File.ReadAllText(resultPath) : string.Empty;
        var success = result.ExitCode == 0
                      && output.Contains($"shared={expectedModValue}", StringComparison.Ordinal)
                      && output.Contains($"dynamic={expectedDynamicValue}", StringComparison.Ordinal)
                      && output.Contains("base-only=base", StringComparison.Ordinal)
                      && output.Contains("mod-only=mod", StringComparison.Ordinal)
                      && output.Contains("nested=mod-system", StringComparison.Ordinal)
                      && output.Contains("bootstrap=bootstrap", StringComparison.Ordinal)
                      && output.Contains("profile-file=profile-fsgame", StringComparison.Ordinal);

        if (!success)
        {
            Console.Error.WriteLine(
                $"Managed USVFS PoC failed at iteration {iteration}. ExitCode={result.ExitCode}, ProcessId={result.ProcessId}");
            Console.Error.WriteLine(output);
            Console.Error.WriteLine($"PoC files: {root}");
            return 20;
        }

        Console.WriteLine($"USVFS iteration {iteration}/{iterations} passed ({expectedModValue}).");
    }

    Console.WriteLine($"Managed USVFS PoC passed {iterations} iteration(s). Files: {root}");
    return 0;
}

static async Task VerifyWritableDirectoryMappingAsync(
    IUsvfsRuntime runtime,
    string root,
    string childExecutable)
{
    var scenarioRoot = Path.Combine(root, "writable-directory-regression");
    var game = Path.Combine(scenarioRoot, "game");
    var mod = Path.Combine(scenarioRoot, "mod");
    var workspace = Path.Combine(scenarioRoot, "workspace");
    var resultPath = Path.Combine(scenarioRoot, "result.txt");
    WriteText(
        Path.Combine(game, "fsgame.ltx"),
        "$app_data_root$ = true | false | $fs_root$ | userdata\\");
    WriteText(Path.Combine(game, "gamedata", "configs", "localization.ltx"), "base-localization");
    WriteText(Path.Combine(mod, "gamedata", "textures", "marker.txt"), "mod-texture");

    var profile = new ModProfile { GameInstallPath = game };
    profile.Mods.Add(new ModEntry
    {
        Id = "sparse-mod",
        Name = "Sparse mod",
        SourcePath = mod,
        IsEnabled = true,
        Order = 1
    });
    var layers = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
    var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, workspace);
    var localization = manifest.WritableFiles.Single(file =>
        file.RelativePath == Path.Combine("gamedata", "configs", "localization.ltx"));
    WriteText(localization.StoragePath, "profile-localization");
    Directory.CreateDirectory(manifest.WriteOverlayRoot);

    var plan = UsvfsMappingPlanBuilder.Build(layers, manifest);
    var virtualLocalization = Path.Combine(game, "gamedata", "configs", "localization.ltx");
    var result = await runtime.RunAsync(
        plan,
        new UsvfsProcessLaunchRequest(
            childExecutable,
            $"--read {Quote(virtualLocalization)} {Quote(resultPath)}",
            AppContext.BaseDirectory),
        new UsvfsRuntimeOptions(
            "stalker_launcher_writable_directory_regression",
            LogToConsole: false,
            DiagnosticLogPath: Path.Combine(scenarioRoot, "usvfs.log")));

    if (result.ExitCode != 0 || ReadText(resultPath) != "profile-localization")
    {
        throw new InvalidOperationException("USVFS writable-directory regression test failed.");
    }

    Console.WriteLine("USVFS writable-directory regression passed.");
}

static int RunLauncher(string[] args)
{
    if (args.Length != 3)
    {
        return 40;
    }

    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
    Process.Start(new ProcessStartInfo
    {
        FileName = executable,
        Arguments = $"--child {Quote(args[1])} {Quote(args[2])}",
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = false
    });
    return 0;
}

static int RunChild(string[] args)
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: StalkerUsvfsManagedPoc --child <virtual-root> <result-file>");
        return 30;
    }

    var virtualRoot = args[1];
    var resultPath = args[2];
    Thread.Sleep(500);
    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

    File.WriteAllText(
        resultPath,
        string.Join(
            Environment.NewLine,
            [
                "shared=" + ReadText(Path.Combine(virtualRoot, "shared.txt")),
                "dynamic=" + ReadText(Path.Combine(virtualRoot, "dynamic.txt")),
                "base-only=" + ReadText(Path.Combine(virtualRoot, "base-only.txt")),
                "mod-only=" + ReadText(Path.Combine(virtualRoot, "mod-only.txt")),
                "nested=" + ReadText(Path.Combine(virtualRoot, "gamedata", "config", "system.ltx")),
                "bootstrap=" + ReadText(Path.Combine(virtualRoot, "physical-bootstrap.txt")),
                "profile-file=" + ReadText(Path.Combine(virtualRoot, "fsgame.ltx"))
            ]) + Environment.NewLine);

    return 0;
}

static int RunRead(string[] args)
{
    if (args.Length != 3)
    {
        return 50;
    }

    WriteText(args[2], ReadText(args[1]));
    return 0;
}

static void CopyUsvfsRuntime(string usvfsRoot, string outputDirectory)
{
    CopyRequired(Path.Combine(usvfsRoot, "lib", "usvfs_x64.dll"), Path.Combine(outputDirectory, "usvfs_x64.dll"));
    CopyRequired(Path.Combine(usvfsRoot, "bin", "usvfs_proxy_x64.exe"), Path.Combine(outputDirectory, "usvfs_proxy_x64.exe"));
    CopyRequired(Path.Combine(usvfsRoot, "lib", "usvfs_x86.dll"), Path.Combine(outputDirectory, "usvfs_x86.dll"));
    CopyRequired(Path.Combine(usvfsRoot, "bin", "usvfs_proxy_x86.exe"), Path.Combine(outputDirectory, "usvfs_proxy_x86.exe"));
}

static void CopyRequired(string source, string destination)
{
    if (!File.Exists(source))
    {
        throw new FileNotFoundException("Required USVFS runtime file was not found.", source);
    }

    File.Copy(source, destination, overwrite: true);
}

static void WriteText(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}

static string ReadText(string path)
{
    return File.Exists(path) ? File.ReadAllText(path) : "<missing>";
}

static string Quote(string value)
{
    return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
