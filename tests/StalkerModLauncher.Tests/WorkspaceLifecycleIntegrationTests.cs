using System.Text.Json;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class WorkspaceLifecycleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherLifecycleTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OverlayWorkspaceFullLifecyclePreservesSourcesAndProfileUserData()
    {
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mods", "main");
        var patch = Path.Combine(_root, "mods", "patch");
        var workspaceRoot = Path.Combine(_root, "workspaces");
        var builder = new WorkspaceBuilder(new AppPaths(
            Path.Combine(_root, "config"),
            workspaceRoot,
            preferGameDriveWorkspace: false));
        var profile = CreateProfile(game, mod, patch);

        CreateFile(game, "bin/xr_3da.exe", "base executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(game, "appdata/user.ltx", "base user settings");
        CreateFile(game, "gamedata/config/priority.ltx", "base");
        CreateFile(mod, "bin/xr_3da.exe", "mod executable");
        CreateFile(mod, "gamedata/config/priority.ltx", "main mod");
        CreateFile(patch, "bin/xr_3da.exe", "patch executable");
        CreateFile(patch, "gamedata/config/priority.ltx", "patch");

        var firstProgress = new ProgressLog();
        var first = await builder.BuildAsync(game, profile, firstProgress);
        profile.WorkspacePath = first.ProfileWorkspacePath;

        Assert.Equal("patch", File.ReadAllText(Path.Combine(first.WorkspaceRoot, "gamedata", "config", "priority.ltx")));
        Assert.Equal("patch executable", File.ReadAllText(first.ExecutablePath));
        Assert.Contains(Path.Combine(first.ProfileWorkspacePath, "userdata"), File.ReadAllText(Path.Combine(first.WorkspaceRoot, "fsgame.ltx")));
        Assert.True(File.Exists(Path.Combine(first.ProfileWorkspacePath, "build-manifest.json")));
        AssertManifestHasStatistics(first.ProfileWorkspacePath);
        AssertSourcesUnchanged(game, mod, patch);

        var save = Path.Combine(first.ProfileWorkspacePath, "userdata", "savedgames", "integration.sav");
        CreateFileAtPath(save, "profile save");
        var cachedProgress = new ProgressLog();

        await builder.BuildAsync(game, profile, cachedProgress);

        Assert.Contains(cachedProgress.Messages, message => message.Contains("Workspace уже актуален", StringComparison.Ordinal));
        Assert.Equal("profile save", File.ReadAllText(save));

        builder.ClearProfileWorkspaceCache(profile, game);

        Assert.False(Directory.Exists(first.WorkspaceRoot));
        Assert.False(File.Exists(Path.Combine(first.ProfileWorkspacePath, "build-manifest.json")));
        Assert.Equal("profile save", File.ReadAllText(save));

        var rebuilt = await builder.BuildAsync(game, profile, new ProgressLog());

        Assert.Equal("patch", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "gamedata", "config", "priority.ltx")));
        Assert.Equal("profile save", File.ReadAllText(save));
        AssertSourcesUnchanged(game, mod, patch);

        builder.DeleteProfileWorkspace(profile, game);

        Assert.False(Directory.Exists(first.ProfileWorkspacePath));
        AssertSourcesUnchanged(game, mod, patch);
    }

    [Fact]
    public async Task CapturesNewFilesByRelativePathAndKeepsNewerUsvfsWrites()
    {
        var game = Path.Combine(_root, "game");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = new ModProfile { GameInstallPath = game, LaunchBackendKind = LaunchBackendKind.LinkedWorkspace };
        const string notes = "Interactive_PDA_notes.txt";
        const string routes = @"gamedata\configs\route_hashes.ltx";
        CreateFile(game, @"bin\xr_3da.exe", "executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(game, @"gamedata\configs\source.ltx", "source");
        var built = await builder.BuildAsync(game, profile, new ProgressLog());
        profile.WorkspacePath = built.ProfileWorkspacePath;
        CreateFile(built.WorkspaceRoot, notes, "notes");
        CreateFile(built.WorkspaceRoot, routes, "route hashes");

        profile.LaunchBackendKind = LaunchBackendKind.VirtualFileSystem;
        var plan = FileLayerPlan.CreateForProfile(game, profile, profile.WorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, plan, profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(plan, manifest, profile.WorkspacePath);
        var mapping = UsvfsMappingPlanBuilder.Build(plan, manifest);
        Assert.Contains(mapping.Operations, operation => operation.CreateTarget &&
            operation.SourcePath == manifest.WriteOverlayRoot);
        Assert.Equal("notes", File.ReadAllText(Path.Combine(manifest.WriteOverlayRoot, notes)));
        Assert.Equal("route hashes", File.ReadAllText(Path.Combine(manifest.WriteOverlayRoot, routes)));
        Assert.False(File.Exists(Path.Combine(manifest.WriteOverlayRoot, @"gamedata\configs\source.ltx")));
        Assert.False(File.Exists(Path.Combine(manifest.WriteOverlayRoot, ".writable-baseline.json")));

        CreateFile(manifest.WriteOverlayRoot, notes, "updated by USVFS");
        UsvfsProfileDataPreparer.Prepare(plan, manifest, profile.WorkspacePath);
        profile.LaunchBackendKind = LaunchBackendKind.LinkedWorkspace;
        builder.ClearProfileWorkspaceCache(profile, game);
        var rebuilt = await builder.BuildAsync(game, profile, new ProgressLog());
        Assert.Equal("updated by USVFS", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, notes)));
        Assert.Equal("route hashes", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, routes)));

        CreateFile(rebuilt.WorkspaceRoot, routes, "updated by Workspace");
        CreateFile(game, "new-source.txt", "trigger automatic rebuild");
        var updated = await builder.BuildAsync(game, profile, new ProgressLog());
        Assert.Equal("updated by Workspace", File.ReadAllText(Path.Combine(updated.WorkspaceRoot, routes)));
        Assert.Equal("updated by Workspace", File.ReadAllText(Path.Combine(manifest.WriteOverlayRoot, routes)));
        Assert.False(File.Exists(Path.Combine(game, notes)));
        Assert.False(File.Exists(Path.Combine(game, routes)));
    }

    [Theory]
    [InlineData(@"gamedata\scripts\spatial_anomalies.script", @"gamedata\configs\plugins\spatial_anomalies\srs_id.txt")]
    [InlineData(@"gamedata\scripts\swm_slowmo.script", @"gamedata\_EXE\speed.txt")]
    public async Task ModWriteRulesUseIndependentCopiesOfWinningFiles(string script, string ids)
    {
        var game = Path.Combine(_root, "game");
        var spatial = Path.Combine(_root, "spatial");
        var patch = Path.Combine(_root, "patch");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = CreateProfile(game, spatial, patch);
        profile.LaunchBackendKind = LaunchBackendKind.LinkedWorkspace;
        CreateFile(game, @"bin\xr_3da.exe", "executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(spatial, script, "script");
        CreateFile(spatial, ids, "original");
        CreateFile(patch, ids, "winning source");

        var built = await builder.BuildAsync(game, profile, new ProgressLog());
        profile.WorkspacePath = built.ProfileWorkspacePath;
        Assert.Equal("winning source", File.ReadAllText(Path.Combine(built.WorkspaceRoot, ids)));
        var staged = Path.Combine(built.WorkspaceRoot, ids + ".tmp");
        File.WriteAllText(staged, "profile IDs");
        File.Move(staged, Path.Combine(built.WorkspaceRoot, ids), overwrite: true);
        builder.ClearProfileWorkspaceCache(profile, game);
        Assert.Equal("original", File.ReadAllText(Path.Combine(spatial, ids)));
        Assert.Equal("winning source", File.ReadAllText(Path.Combine(patch, ids)));

        profile.LaunchBackendKind = LaunchBackendKind.VirtualFileSystem;
        var plan = FileLayerPlan.CreateForProfile(game, profile, profile.WorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, plan, profile.WorkspacePath);
        UsvfsProfileDataPreparer.Prepare(plan, manifest, profile.WorkspacePath);
        var stored = Assert.Single(manifest.WritableFiles, file => file.RelativePath == ids);
        Assert.Equal("profile IDs", File.ReadAllText(stored.StoragePath));
        Assert.Contains(UsvfsMappingPlanBuilder.Build(plan, manifest).Operations, operation =>
            operation.SourcePath == stored.StoragePath && operation.DestinationPath == Path.Combine(game, ids));

        var other = CreateProfile(game, spatial, patch);
        var otherWorkspace = new ProfileManager(paths, builder).EnsureProfileFolderPath(other);
        var otherPlan = FileLayerPlan.CreateForProfile(game, other, otherWorkspace);
        var otherManifest = OverlayManifestBuilder.BuildVirtualFileSystem(other, otherPlan, otherWorkspace);
        UsvfsProfileDataPreparer.Prepare(otherPlan, otherManifest, otherWorkspace);
        Assert.Equal("winning source", File.ReadAllText(Assert.Single(otherManifest.WritableFiles,
            file => file.RelativePath == ids).StoragePath));
        other.Mods[0].IsEnabled = false;
        otherPlan = FileLayerPlan.CreateForProfile(game, other, otherWorkspace);
        Assert.DoesNotContain(OverlayManifestBuilder.BuildVirtualFileSystem(other, otherPlan, otherWorkspace).WritableFiles,
            file => file.RelativePath == ids);
    }

    [Fact]
    public async Task WorkspaceKeepsProtectedDeletionAcrossRebuildAndAcceptsRecreation()
    {
        const string relative = @"gamedata\_EXE\speed.txt";
        var game = Path.Combine(_root, "game");
        var mod = Path.Combine(_root, "mod");
        var patch = Path.Combine(_root, "patch");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = CreateProfile(game, mod, patch);
        profile.LaunchBackendKind = LaunchBackendKind.LinkedWorkspace;
        var progress = new ProgressLog();
        CreateFile(game, @"bin\xr_3da.exe", "executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(mod, @"gamedata\scripts\swm_slowmo.script", "script");
        CreateFile(mod, relative, "original");
        Directory.CreateDirectory(patch);

        var built = await builder.BuildAsync(game, profile, progress);
        profile.WorkspacePath = built.ProfileWorkspacePath;
        var current = Path.Combine(built.WorkspaceRoot, relative);
        File.Delete(current);
        builder.ClearProfileWorkspaceCache(profile, game);
        Assert.False(File.Exists(Path.Combine((await builder.BuildAsync(game, profile, progress)).WorkspaceRoot, relative)));
        Assert.False(File.Exists(Path.Combine((await builder.BuildAsync(game, profile, progress)).WorkspaceRoot, relative)));
        Assert.Equal("original", File.ReadAllText(Path.Combine(mod, relative)));

        var other = CreateProfile(game, mod, patch);
        other.LaunchBackendKind = LaunchBackendKind.LinkedWorkspace;
        Assert.Equal("original", File.ReadAllText(Path.Combine((await builder.BuildAsync(game, other, progress)).WorkspaceRoot, relative)));

        File.WriteAllText(current, "original");
        builder.ClearProfileWorkspaceCache(profile, game);
        Assert.Equal("original", File.ReadAllText(Path.Combine((await builder.BuildAsync(game, profile, progress)).WorkspaceRoot, relative)));
    }

    [Fact]
    public async Task ClearKeepsCurrentWhenSavingGeneratedFilesFails()
    {
        var game = Path.Combine(_root, "game");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "workspaces"), false);
        var builder = new WorkspaceBuilder(paths);
        var profile = new ModProfile { GameInstallPath = game, LaunchBackendKind = LaunchBackendKind.LinkedWorkspace };
        CreateFile(game, @"bin\xr_3da.exe", "executable");
        CreateFile(game, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        var built = await builder.BuildAsync(game, profile, new ProgressLog());
        profile.WorkspacePath = built.ProfileWorkspacePath;
        const string notes = "notes.txt";
        CreateFile(built.WorkspaceRoot, notes, "keep this");
        var overwrite = Path.Combine(profile.WorkspacePath, "userdata", "overwrite");
        Directory.CreateDirectory(Path.Combine(overwrite, notes));

        Assert.NotNull(Record.Exception(() => builder.ClearProfileWorkspaceCache(profile, game)));
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(built.WorkspaceRoot, notes)));
        Assert.True(File.Exists(Path.Combine(profile.WorkspacePath, "build-manifest.json")));
    }

    private static ModProfile CreateProfile(string game, string mod, string patch)
    {
        var profile = new ModProfile
        {
            Name = "Integration profile",
            GameInstallPath = game,
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };
        profile.Mods.Add(new ModEntry { Name = "Main mod", SourcePath = mod, IsEnabled = true, Order = 1 });
        profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = patch, IsEnabled = true, Order = 2 });
        return profile;
    }

    private static void AssertManifestHasStatistics(string profileWorkspacePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profileWorkspacePath, "build-manifest.json")));
        var root = document.RootElement;
        Assert.True(root.GetProperty("HasStatistics").GetBoolean());
        Assert.True(root.GetProperty("FileCount").GetInt32() >= 4);
        Assert.True(root.GetProperty("HardLinkCount").GetInt32() + root.GetProperty("SymbolicLinkCount").GetInt32() >= 3);
        Assert.True(root.GetProperty("LocalFileCount").GetInt32() >= 1);
    }

    private static void AssertSourcesUnchanged(string game, string mod, string patch)
    {
        Assert.Equal("base", File.ReadAllText(Path.Combine(game, "gamedata", "config", "priority.ltx")));
        Assert.Equal("main mod", File.ReadAllText(Path.Combine(mod, "gamedata", "config", "priority.ltx")));
        Assert.Equal("patch", File.ReadAllText(Path.Combine(patch, "gamedata", "config", "priority.ltx")));
        Assert.Equal("$app_data_root$ = true | false | appdata", File.ReadAllText(Path.Combine(game, "fsgame.ltx")));
        Assert.Equal("base executable", File.ReadAllText(Path.Combine(game, "bin", "xr_3da.exe")));
        Assert.Equal("mod executable", File.ReadAllText(Path.Combine(mod, "bin", "xr_3da.exe")));
        Assert.Equal("patch executable", File.ReadAllText(Path.Combine(patch, "bin", "xr_3da.exe")));
    }

    private static void CreateFile(string root, string relativePath, string content)
    {
        CreateFileAtPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
    }

    private static void CreateFileAtPath(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ProgressLog : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value)
        {
            Messages.Add(value);
        }
    }
}
