using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class MiseryUsvfsTests : IDisposable
{
    private static readonly string[] Classes = ["assaulter", "recon", "sniper"];
    private const string Actor = "gamedata/configs/creatures/actor.ltx";
    private const string Fsgame = "$app_data_root$ = true | false | $fs_root$ | appdata\\\n$game_data$ = true | true | $fs_root$ | gamedata\\\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherMiseryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkspaceKeepsClassChangesThroughCacheReuseClearAndMove()
    {
        var game = CreateGame();
        var mod = CreateDirectory("misery");
        AddClasses(mod);
        Write(game, Actor, "base class");
        Write(game, "bin/xr_3da.exe", "workspace fixture");
        Write(mod, Actor, "initial class from the mod");
        const string newTarget = "gamedata/configs/class_only/new_destination.ltx";
        WriteClass(mod, "recon", newTarget, "new recon content");
        var sources = Snapshot(game, mod);
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var manager = new WorkspaceManagementService(builder);
        var profile = new ModProfile { GameInstallPath = game, LaunchBackendKind = LaunchBackendKind.LinkedWorkspace };
        AddMod(profile, mod, 1);
        var progress = new Progress<string>();

        var first = await builder.BuildAsync(game, profile, progress);
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var current = first.WorkspaceRoot;
        var manifest = Path.Combine(first.ProfileWorkspacePath, "build-manifest.json");
        var firstManifest = File.ReadAllBytes(manifest);
        var actor = Path.Combine(current, Actor);
        var createdByGame = Path.Combine(current, newTarget);
        AssertDefaultAliases(Path.Combine(current, "fsgame.ltx"));
        Assert.Equal("initial class from the mod", File.ReadAllText(actor));
        Assert.False(File.Exists(createdByGame));
        Assert.True(Directory.Exists(Path.GetDirectoryName(createdByGame)));
        File.WriteAllBytes(actor, File.ReadAllBytes(ClassPath(mod, "recon", Actor)));
        File.Copy(ClassPath(mod, "recon", newTarget), createdByGame);
        AssertSourcesUnchanged(sources);

        await builder.BuildAsync(game, profile, progress);

        Assert.Equal(firstManifest, File.ReadAllBytes(manifest));
        Assert.Equal("actor_class = recon", ReadStored(profile, Actor));
        Assert.Equal("new recon content", ReadStored(profile, newTarget));
        AssertDefaultAliases(Path.Combine(current, "fsgame.ltx"));

        File.WriteAllBytes(actor, File.ReadAllBytes(ClassPath(mod, "sniper", Actor)));
        manager.ClearCache(profile);
        await builder.BuildAsync(game, profile, progress);
        Assert.Equal("actor_class = sniper", File.ReadAllText(actor));
        Assert.Equal("new recon content", File.ReadAllText(createdByGame));
        AssertDefaultAliases(Path.Combine(current, "fsgame.ltx"));

        File.WriteAllBytes(actor, File.ReadAllBytes(ClassPath(mod, "assaulter", Actor)));
        await manager.MoveAsync(profile, Path.Combine(_root, "moved"), progress, () => Task.CompletedTask);
        var moved = await builder.BuildAsync(game, profile, progress);
        Assert.Equal("actor_class = assaulter", File.ReadAllText(Path.Combine(moved.WorkspaceRoot, Actor)));
        Assert.Equal("new recon content", File.ReadAllText(Path.Combine(moved.WorkspaceRoot, newTarget)));
        AssertDefaultAliases(Path.Combine(moved.WorkspaceRoot, "fsgame.ltx"));
        AssertSourcesUnchanged(sources);
        Assert.False(File.Exists(Path.Combine(mod, newTarget)));
    }

    [Fact]
    public void FirstAndRepeatedPrepareProvideAllClassAliasesWithoutDuplicates()
    {
        var game = CreateGame();
        AddClasses(game);
        var profile = CreateProfile(game, "first");
        var original = File.ReadAllBytes(Path.Combine(game, "fsgame.ltx"));

        var first = Prepare(profile);
        AssertDefaultAliases(Path.Combine(first.WriteOverlayRoot, "fsgame.ltx"));
        var second = Prepare(profile);
        AssertDefaultAliases(Path.Combine(second.WriteOverlayRoot, "fsgame.ltx"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "fsgame.ltx")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedAndCompatibilityConfigsPreserveCustomAliasesAndCp1251(bool manualSelection)
    {
        var game = CreateGame();
        AddClasses(game);
        var profile = CreateProfile(game, "selected");
        var selectedName = manualSelection ? "custom_source.ltx" : "selected.ltx";
        const string customAlias = "$GAME_CLASS_RECON$ = false | true | $fs_root$ | custom_recon\\";
        const string comment = "; $game_class_sniper$ = false | false | commented_out\\";
        const string russian = "; Настройки класса: разведчик";
        var selected = Write(game, selectedName, Fsgame + customAlias + "\n" + comment + "\n" + russian + "\n");
        var original = File.ReadAllBytes(selected);
        profile.LaunchArguments = "-fsltx selected.ltx";
        if (manualSelection)
            profile.FsgameSourcePath = selected;

        Prepare(profile);
        var manifest = Prepare(profile);

        foreach (var name in new[] { selectedName, "selected.ltx", "fsgame.ltx" }.Distinct())
        {
            var destination = Path.Combine(manifest.WriteOverlayRoot, name);
            var lines = File.ReadAllLines(destination, XRayTextEncoding.Config);
            Assert.Contains(customAlias, lines);
            Assert.Contains(comment, lines);
            Assert.Contains(russian, lines);
            Assert.Equal(customAlias, Assert.Single(AliasDefinitions(lines, "recon")));
            AssertDefaultAlias(lines, "assaulter");
            AssertDefaultAlias(lines, "sniper");
        }
        Assert.Equal(original, File.ReadAllBytes(selected));
    }

    [Fact]
    public void ClassUnionSeedsFinalLayerFilesAndKeepsSelectionsIndependentAcrossProfiles()
    {
        var game = CreateGame();
        AddClasses(game);
        var lower = CreateDirectory("lower-mod");
        var upper = CreateDirectory("upper-mod");
        const string reconOnly = "gamedata/scripts/recon_only.script";
        const string sniperOnly = "gamedata/textures/sniper_only.dds";
        WriteClass(lower, "recon", reconOnly, "recon template must not seed");
        WriteClass(upper, "sniper", sniperOnly, "sniper template must not seed");
        Write(game, Actor, "actor_class = assaulter; base");
        Write(lower, Actor, "actor_class = assaulter; lower");
        Write(upper, Actor, "actor_class = assaulter; final");
        Write(game, reconOnly, "base recon target");
        Write(lower, reconOnly, "final recon target");
        Write(upper, sniperOnly, "final sniper target");
        var sourceBytes = Snapshot(game, lower, upper);
        var first = CreateProfile(game, "first");
        var second = CreateProfile(game, "second");
        foreach (var profile in new[] { first, second })
        {
            AddMod(profile, lower, 1);
            AddMod(profile, upper, 2);
            Prepare(profile);
            Assert.Equal("actor_class = assaulter; final", ReadStored(profile, Actor));
            Assert.Equal("final recon target", ReadStored(profile, reconOnly));
            Assert.Equal("final sniper target", ReadStored(profile, sniperOnly));
        }

        var reconBytes = File.ReadAllBytes(ClassPath(game, "recon", Actor));
        File.WriteAllBytes(Stored(first, Actor), reconBytes);
        Prepare(first);
        Prepare(second);

        Assert.Equal(reconBytes, File.ReadAllBytes(Stored(first, Actor)));
        Assert.Equal("actor_class = assaulter; final", ReadStored(second, Actor));
        AssertSourcesUnchanged(sourceBytes);
    }

    [Fact]
    public void MissingInitialDestinationIsNotFabricatedAndCanBeCreatedProfileLocally()
    {
        var game = CreateGame();
        AddClasses(game);
        const string newTarget = "gamedata/configs/new_class_destination.ltx";
        WriteClass(game, "recon", newTarget, "new recon content");
        var first = CreateProfile(game, "first");
        var second = CreateProfile(game, "second");

        Prepare(first);
        Prepare(second);
        Assert.False(File.Exists(Stored(first, newTarget)));
        Assert.False(File.Exists(Stored(second, newTarget)));
        Assert.False(File.Exists(Path.Combine(game, newTarget)));
        var overwrite = Path.Combine(first.WorkspacePath, "userdata", "overwrite");
        Assert.False(File.Exists(Path.Combine(overwrite, newTarget)));

        var newlyCreated = Write(overwrite, newTarget, "created by recon switch");
        Prepare(first);
        Prepare(second);

        Assert.Equal("created by recon switch", ReadStored(first, newTarget));
        Assert.False(File.Exists(newlyCreated));
        Assert.False(File.Exists(Stored(second, newTarget)));
        Assert.False(File.Exists(Path.Combine(game, newTarget)));
    }

    [Fact]
    public void LegacyOverwriteClassDestinationMigratesWithoutLosingSelectedContents()
    {
        var game = CreateGame();
        AddClasses(game);
        Write(game, Actor, "actor_class = assaulter");
        var profile = CreateProfile(game, "legacy");
        var overwriteRoot = Path.Combine(profile.WorkspacePath, "userdata", "overwrite");
        var legacy = Write(overwriteRoot, Actor, "actor_class = recon; legacy selection");
        var original = File.ReadAllBytes(legacy);
        const string unrelated = "gamedata/configs/unrelated.ltx";
        Write(overwriteRoot, unrelated, "unrelated overwrite");

        Prepare(profile);
        Prepare(profile);

        Assert.Equal(original, File.ReadAllBytes(Stored(profile, Actor)));
        Assert.False(File.Exists(legacy));
        Assert.Equal("unrelated overwrite", File.ReadAllText(Path.Combine(overwriteRoot, unrelated), XRayTextEncoding.Config));
        Assert.Equal("actor_class = assaulter", File.ReadAllText(Path.Combine(game, Actor), XRayTextEncoding.Config));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("disabled")]
    [InlineData("excluded")]
    public void IncompleteEffectiveClassTreeDoesNotChangeConfigsOrPrivatizeClassTargets(string unavailable)
    {
        var game = CreateGame();
        WriteClass(game, "assaulter", Actor, "assaulter template");
        WriteClass(game, "recon", Actor, "recon template");
        Write(game, Actor, "unchanged base destination");
        var profile = CreateProfile(game, "incomplete");
        if (unavailable == "empty")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClassPath(game, "sniper", Actor))!);
        }
        else if (unavailable is "disabled" or "excluded")
        {
            var mod = CreateDirectory("sniper-mod");
            WriteClass(mod, "sniper", Actor, "sniper template");
            var entry = AddMod(profile, mod, 1);
            entry.IsEnabled = unavailable != "disabled";
            if (unavailable == "excluded")
                entry.ExcludedFiles.Add(Path.GetRelativePath(mod, ClassPath(mod, "sniper", Actor)));
        }

        var manifest = Prepare(profile);

        var lines = File.ReadAllLines(Path.Combine(manifest.WriteOverlayRoot, "fsgame.ltx"), XRayTextEncoding.Config);
        foreach (var className in Classes)
            Assert.Empty(AliasDefinitions(lines, className));
        Assert.False(File.Exists(Stored(profile, Actor)));
        Assert.Equal("unchanged base destination", File.ReadAllText(Path.Combine(game, Actor), XRayTextEncoding.Config));
    }

    [Fact]
    public void ExcludedClassTemplateDoesNotCreateAnUnrelatedWritableDestination()
    {
        var game = CreateGame();
        AddClasses(game);
        var mod = CreateDirectory("excluded-template-mod");
        const string excludedTarget = "gamedata/scripts/excluded_only.script";
        var template = WriteClass(mod, "recon", excludedTarget, "excluded template");
        Write(game, excludedTarget, "ordinary game script");
        Write(game, Actor, "actor_class = assaulter");
        var profile = CreateProfile(game, "excluded-template");
        AddMod(profile, mod, 1).ExcludedFiles.Add(Path.GetRelativePath(mod, template));

        var manifest = Prepare(profile);

        AssertDefaultAliases(Path.Combine(manifest.WriteOverlayRoot, "fsgame.ltx"));
        Assert.Equal("actor_class = assaulter", ReadStored(profile, Actor));
        Assert.False(File.Exists(Stored(profile, excludedTarget)));
        Assert.Equal("ordinary game script", File.ReadAllText(Path.Combine(game, excludedTarget), XRayTextEncoding.Config));
    }

    [Fact]
    public void ExcludedFinalDestinationFallsBackToTheLastEffectiveSource()
    {
        var game = CreateGame();
        AddClasses(game);
        Write(game, Actor, "base destination");
        var lower = CreateDirectory("lower");
        var upper = CreateDirectory("upper");
        Write(lower, Actor, "effective lower destination");
        Write(upper, Actor, "excluded upper destination");
        var profile = CreateProfile(game, "excluded-destination");
        AddMod(profile, lower, 1);
        AddMod(profile, upper, 2).ExcludedFiles.Add(Actor.Replace('/', '\\'));

        Prepare(profile);

        Assert.Equal("effective lower destination", ReadStored(profile, Actor));
        Assert.Equal("excluded upper destination", File.ReadAllText(Path.Combine(upper, Actor), XRayTextEncoding.Config));
    }

    private string CreateGame()
    {
        var game = CreateDirectory("game");
        Write(game, "fsgame.ltx", Fsgame);
        return game;
    }

    private string CreateDirectory(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private ModProfile CreateProfile(string game, string id) => new()
    {
        Id = id,
        Name = id,
        GameInstallPath = game,
        WorkspacePath = CreateDirectory("workspace-" + id),
        LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
    };

    private static ModEntry AddMod(ModProfile profile, string source, int order)
    {
        var mod = new ModEntry { Id = Path.GetFileName(source), Name = Path.GetFileName(source), SourcePath = source, IsEnabled = true, Order = order };
        profile.Mods.Add(mod);
        return mod;
    }

    private static OverlayManifest Prepare(ModProfile profile)
    {
        var layers = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, profile.WorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(layers, manifest, profile.WorkspacePath);
        return manifest;
    }

    private static void AddClasses(string source)
    {
        foreach (var className in Classes)
            WriteClass(source, className, Actor, "actor_class = " + className);
    }

    private static string ClassPath(string source, string className, string target) =>
        Path.Combine(source, "gamedata", "class_diversity", className, target);

    private static string WriteClass(string source, string className, string target, string content) =>
        Write(source, Path.GetRelativePath(source, ClassPath(source, className, target)), content);

    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, XRayTextEncoding.Config);
        return path;
    }

    private static string Stored(ModProfile profile, string target) =>
        Path.Combine(profile.WorkspacePath, "userdata", "writable-game-files", target);

    private static string ReadStored(ModProfile profile, string target) =>
        File.ReadAllText(Stored(profile, target), XRayTextEncoding.Config);

    private static IEnumerable<string> AliasDefinitions(IEnumerable<string> lines, string className) =>
        lines.Where(line => line.Split('=', 2)[0].Trim().Equals("$game_class_" + className + "$", StringComparison.OrdinalIgnoreCase));

    private static void AssertDefaultAliases(string path)
    {
        var lines = File.ReadAllLines(path, XRayTextEncoding.Config);
        foreach (var className in Classes)
            AssertDefaultAlias(lines, className);
    }

    private static void AssertDefaultAlias(string[] lines, string className)
    {
        var definition = Assert.Single(AliasDefinitions(lines, className));
        var fields = definition.Split('=', 2)[1].Split('|').Select(field => field.Trim()).ToArray();
        Assert.Equal(new[] { "true", "false", "$game_data$", "class_diversity\\" + className + "\\" }, fields);
    }

    private static Dictionary<string, byte[]> Snapshot(params string[] roots) =>
        roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertSourcesUnchanged(Dictionary<string, byte[]> snapshot)
    {
        foreach (var (path, bytes) in snapshot)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
