using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

// This research executable only uses its own generated fixture; it never loads saved profiles.
if (args.Length == 0)
{
    throw new ArgumentException("Usage: (--fixture | --fixture-sparse) <new-directory> | --host <runtime-root> <fixture> <result> | --child <fixture> <result>");
}

switch (args[0])
{
    case "--fixture" when args.Length == 2:
        CreateFixture(Path.GetFullPath(args[1]));
        break;
    case "--fixture-sparse" when args.Length == 2:
        CreateFixture(Path.GetFullPath(args[1]), sparse: true);
        break;
    case "--host" when args.Length == 4:
        await RunHost(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        break;
    case "--child" when args.Length == 3:
        RunChild(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        break;
    default:
        throw new ArgumentException("Invalid benchmark arguments.");
}

static void CreateFixture(string root, bool sparse = false)
{
    if (Directory.Exists(root))
    {
        throw new IOException("Fixture directory must not exist: " + root);
    }

    for (var index = 0; index < (sparse ? 0 : 1000); index++)
    {
        Write(Path.Combine(root, "game", RelativeFile(index)), "base");
    }

    for (var mod = 0; mod < 40; mod++)
    {
        var modRoot = Path.Combine(root, $"mod-{mod:D2}");
        Directory.CreateDirectory(modRoot);
        if (sparse)
        {
            if (mod < 4)
            {
                for (var index = 0; index < 2; index++)
                    Write(Path.Combine(modRoot, "gamedata", "configs", $"a-selected-{mod}-{index}.ltx"), $"mod-{mod}");
                for (var index = 0; index < 10000; index++)
                    Write(Path.Combine(modRoot, "gamedata", "configs", $"z-unused-{mod}-{index:D5}.ltx"), $"mod-{mod}");
            }
            continue;
        }
        for (var index = 0; index < 500; index++)
        {
            Write(Path.Combine(modRoot, RelativeFile(index + mod * 250)), $"mod-{mod}");
        }

        Write(Path.Combine(modRoot, "gamedata", "configs", "shared.ltx"), $"mod-{mod}");
    }

    Write(Path.Combine(root, "game", "gamedata", "configs", "кириллица.ltx"), "unicode");
    Write(Path.Combine(root, "game", "physical-only.txt"), "physical");
    var layerPlan = CreateLayerPlan(root, sparse);
    var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(CreateProfile(root, sparse), layerPlan, Path.Combine(root, "workspace"));
    Directory.CreateDirectory(manifest.WriteOverlayRoot);
    Write(Path.Combine(manifest.WriteOverlayRoot, "gamedata", "configs", "overwrite.ltx"), "overwrite");

    var expected = layerPlan.SourceLayers
        .SelectMany(layer => Directory.EnumerateFiles(layer.RootPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(layer.RootPath, file)))
        .Where(path => path.StartsWith("gamedata" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .Append(Path.Combine("gamedata", "configs", "overwrite.ltx"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    // Resolve sampled winners with the launcher's plan, rather than duplicating mod priority rules.
    var samples = expected.Where((path, index) => index % 103 == 0 && !path.EndsWith("overwrite.ltx", StringComparison.Ordinal))
        .Concat(expected.Where(path => sparse ? Path.GetFileName(path).StartsWith("a-selected-", StringComparison.Ordinal) : path.EndsWith("shared.ltx", StringComparison.Ordinal)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(path => path, path => File.ReadAllText(layerPlan.FindFinalFile(path)!.FullPath));
    File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new Fixture(expected, samples, sparse)));
    Console.WriteLine($"Fixture: {(sparse ? "sparse selection, 4 x 10,002 source files" : "40 overlapping mods")}, {expected.Length} final gamedata files.");
}

static ModProfile CreateProfile(string root, bool sparse = false)
{
    var profile = new ModProfile { Id = "directory-benchmark", Name = "Directory benchmark", GameInstallPath = Path.Combine(root, "game") };
    for (var mod = 0; mod < 40; mod++)
    {
        profile.Mods.Add(new ModEntry
        {
            Id = $"mod-{mod}",
            Name = $"mod-{mod}",
            SourcePath = Path.Combine(root, $"mod-{mod:D2}"),
            Order = mod + 1,
            IsEnabled = true
        });
    }

    if (!sparse) profile.Mods[^1].ExcludedFiles.Add(Path.Combine("gamedata", "configs", "shared.ltx"));
    return profile;
}

static FileLayerPlan CreateLayerPlan(string root, bool sparse = false) =>
    FileLayerPlan.CreateForProfile(Path.Combine(root, "game"), CreateProfile(root, sparse), Path.Combine(root, "workspace"));

static async Task RunHost(string runtimeRoot, string root, string resultPath)
{
    var fixture = ReadFixture(root);
    foreach (var name in new[] { "usvfs_x64.dll", "usvfs_x86.dll", "usvfs_proxy_x64.exe", "usvfs_proxy_x86.exe" })
    {
        File.Copy(Path.Combine(runtimeRoot, name.EndsWith(".dll", StringComparison.Ordinal) ? "lib" : "bin", name),
            Path.Combine(AppContext.BaseDirectory, name), overwrite: true);
    }

    var profile = CreateProfile(root, fixture.Sparse);
    var layerPlan = CreateLayerPlan(root, fixture.Sparse);
    var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layerPlan, Path.Combine(root, "workspace"));
    var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);
    var runtime = new UsvfsRuntime(new OfficialUsvfsNativeApi());
    var watch = Stopwatch.StartNew();
    await using var session = runtime.CreateSession(plan,
        new UsvfsRuntimeOptions("cordon_directory_benchmark", LogToConsole: false,
            DiagnosticLogPath: resultPath + ".usvfs.log"));
    var preparationMs = watch.Elapsed.TotalMilliseconds;
    var executable = Path.Combine(AppContext.BaseDirectory, "StalkerUsvfsDirectoryBenchmark.exe");
    using var process = session.StartProcess(new UsvfsProcessLaunchRequest(executable,
        $"--child \"{root}\" \"{resultPath}\"", AppContext.BaseDirectory));
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    int exitCode;
    try
    {
        exitCode = await session.GetExitCodeAsync(timeout.Token);
    }
    catch
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        throw;
    }

    if (exitCode != 0) throw new InvalidOperationException($"Hooked child failed: {exitCode}");
    var result = JsonSerializer.Deserialize<Measurement>(File.ReadAllText(resultPath))!;
    File.WriteAllText(resultPath, JsonSerializer.Serialize(result with { PreparationMs = preparationMs }));
    if (File.Exists(Path.Combine(root, "game", "gamedata", "configs", "benchmark-write.tmp")))
    {
        throw new InvalidOperationException("A write escaped the overwrite directory.");
    }
    var storedWrite = Path.Combine(manifest.WriteOverlayRoot, "gamedata", "configs", "benchmark-write.tmp");
    if (File.ReadAllText(storedWrite) != "persist") throw new InvalidOperationException("Write was not captured by overwrite.");
    File.Delete(storedWrite);
    Console.WriteLine(File.ReadAllText(resultPath));
}

static void RunChild(string root, string resultPath)
{
    var fixture = ReadFixture(root);
    var game = Path.Combine(root, "game");
    var pattern = fixture.Sparse ? "a-selected-*" : "*";
    var expected = fixture.Paths.Where(path => !fixture.Sparse || Path.GetFileName(path).StartsWith("a-selected-", StringComparison.Ordinal)).ToArray();
    var times = new List<double>();
    for (var run = 0; run < 8; run++)
    {
        var watch = Stopwatch.StartNew();
        var files = fixture.Sparse
            ? NativeFileSearch.List(Path.Combine(game, "gamedata", "configs"), pattern)
            : Directory.EnumerateFiles(Path.Combine(game, "gamedata"), pattern, SearchOption.AllDirectories).ToArray();
        watch.Stop();
        var relative = files.Select(file => Path.GetRelativePath(game, file)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!relative.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Enumeration mismatch: expected {expected.Length}, got {relative.Length} (duplicates: {relative.Length - relative.Distinct(StringComparer.OrdinalIgnoreCase).Count()}).");
        }
        if (run != 0) times.Add(watch.Elapsed.TotalMilliseconds);
    }

    foreach (var (relative, content) in fixture.Samples)
    {
        if (File.ReadAllText(Path.Combine(game, relative)) != content)
            throw new InvalidOperationException("Wrong winning file: " + relative);
    }
    var configs = Path.Combine(game, "gamedata", "configs");
    var allFiles = Directory.EnumerateFiles(Path.Combine(game, "gamedata"), "*.ltx", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(game, file)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    if (File.ReadAllText(Path.Combine(configs, "overwrite.ltx")) != "overwrite" ||
        File.ReadAllText(Path.Combine(game, "physical-only.txt")) != "physical" ||
        !allFiles.SequenceEqual(fixture.Paths, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Overwrite, physical file or wildcard check failed.");
    var writable = Path.Combine(configs, "benchmark-write.tmp");
    var replacement = writable + ".replacement";
    File.WriteAllText(writable, "first");
    File.WriteAllText(replacement, "second");
    File.Move(replacement, writable, overwrite: true);
    if (File.ReadAllText(writable) != "second") throw new InvalidOperationException("Atomic replacement failed.");
    File.Delete(writable);
    if (File.Exists(writable)) throw new InvalidOperationException("Delete failed.");
    File.WriteAllText(writable, "persist");
    times.Sort();
    File.WriteAllText(resultPath, JsonSerializer.Serialize(new Measurement(expected.Length, times, times[times.Count / 2], 0)));
}

static Fixture ReadFixture(string root) =>
    JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(root, "fixture.json")))!;

static string RelativeFile(int index) => Path.Combine("gamedata", "configs", $"group-{index % 20:D2}", $"file-{index:D5}.ltx");

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

internal sealed record Fixture(string[] Paths, Dictionary<string, string> Samples, bool Sparse = false);
internal sealed record Measurement(int FileCount, List<double> EnumerationMs, double MedianMs, double PreparationMs);

internal static class NativeFileSearch
{
    internal static string[] List(string directory, string pattern)
    {
        // Pass the pattern to Windows: .NET enumeration filters names after reading them.
        var handle = FindFirstFileW(Path.Combine(directory, pattern), out var data);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var files = new List<string>();
            do { files.Add(Path.Combine(directory, data.FileName)); }
            while (FindNextFileW(handle, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
            return files.ToArray();
        }
        finally { FindClose(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string pattern, out FindData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr handle, out FindData data);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
