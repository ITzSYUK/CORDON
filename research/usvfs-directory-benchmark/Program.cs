using System.Diagnostics;
using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;

// This research executable only uses its own generated fixture; it never loads saved profiles.
if (args.Length == 0)
{
    throw new ArgumentException("Usage: --fixture <new-directory> | --host <runtime-root> <fixture> <result> | --child <fixture> <result>");
}

switch (args[0])
{
    case "--fixture" when args.Length == 2:
        CreateFixture(Path.GetFullPath(args[1]));
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

static void CreateFixture(string root)
{
    if (Directory.Exists(root))
    {
        throw new IOException("Fixture directory must not exist: " + root);
    }

    for (var index = 0; index < 1000; index++)
    {
        Write(Path.Combine(root, "game", RelativeFile(index)), "base");
    }

    for (var mod = 0; mod < 40; mod++)
    {
        var modRoot = Path.Combine(root, $"mod-{mod:D2}");
        for (var index = 0; index < 500; index++)
        {
            Write(Path.Combine(modRoot, RelativeFile(index + mod * 250)), $"mod-{mod}");
        }

        Write(Path.Combine(modRoot, "gamedata", "configs", "shared.ltx"), $"mod-{mod}");
    }

    Write(Path.Combine(root, "game", "gamedata", "configs", "кириллица.ltx"), "unicode");
    Write(Path.Combine(root, "game", "physical-only.txt"), "physical");
    var layerPlan = CreateLayerPlan(root);
    var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(CreateProfile(root), layerPlan, Path.Combine(root, "workspace"));
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
        .Append(Path.Combine("gamedata", "configs", "shared.ltx"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(path => path, path => File.ReadAllText(layerPlan.FindFinalFile(path)!.FullPath));
    File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new Fixture(expected, samples)));
    Console.WriteLine($"Fixture: 40 mods, 20,000 mod files, {expected.Length} final gamedata files.");
}

static ModProfile CreateProfile(string root)
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

    profile.Mods[^1].ExcludedFiles.Add(Path.Combine("gamedata", "configs", "shared.ltx"));
    return profile;
}

static FileLayerPlan CreateLayerPlan(string root) =>
    FileLayerPlan.CreateLinkedWorkspace(Path.Combine(root, "game"), CreateProfile(root), Path.Combine(root, "workspace"));

static async Task RunHost(string runtimeRoot, string root, string resultPath)
{
    _ = ReadFixture(root);
    foreach (var name in new[] { "usvfs_x64.dll", "usvfs_x86.dll", "usvfs_proxy_x64.exe", "usvfs_proxy_x86.exe" })
    {
        File.Copy(Path.Combine(runtimeRoot, name.EndsWith(".dll", StringComparison.Ordinal) ? "lib" : "bin", name),
            Path.Combine(AppContext.BaseDirectory, name), overwrite: true);
    }

    var profile = CreateProfile(root);
    var layerPlan = CreateLayerPlan(root);
    var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, Path.Combine(root, "workspace"));
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
    var times = new List<double>();
    for (var run = 0; run < 8; run++)
    {
        var watch = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(Path.Combine(game, "gamedata"), "*", SearchOption.AllDirectories).ToArray();
        watch.Stop();
        var relative = files.Select(file => Path.GetRelativePath(game, file)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!relative.SequenceEqual(fixture.Paths, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Enumeration mismatch: expected {fixture.Paths.Length}, got {relative.Length} (duplicates: {relative.Length - relative.Distinct(StringComparer.OrdinalIgnoreCase).Count()}).");
        }
        if (run != 0) times.Add(watch.Elapsed.TotalMilliseconds);
    }

    foreach (var (relative, content) in fixture.Samples)
    {
        if (File.ReadAllText(Path.Combine(game, relative)) != content)
            throw new InvalidOperationException("Wrong winning file: " + relative);
    }
    var configs = Path.Combine(game, "gamedata", "configs");
    if (File.ReadAllText(Path.Combine(configs, "overwrite.ltx")) != "overwrite" ||
        File.ReadAllText(Path.Combine(game, "physical-only.txt")) != "physical" ||
        Directory.EnumerateFiles(Path.Combine(game, "gamedata"), "*.ltx", SearchOption.AllDirectories).Count() != fixture.Paths.Length)
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
    File.WriteAllText(resultPath, JsonSerializer.Serialize(new Measurement(fixture.Paths.Length, times, times[times.Count / 2], 0)));
}

static Fixture ReadFixture(string root) =>
    JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(root, "fixture.json")))!;

static string RelativeFile(int index) => Path.Combine("gamedata", "configs", $"group-{index % 20:D2}", $"file-{index:D5}.ltx");

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

internal sealed record Fixture(string[] Paths, Dictionary<string, string> Samples);
internal sealed record Measurement(int FileCount, List<double> EnumerationMs, double MedianMs, double PreparationMs);
