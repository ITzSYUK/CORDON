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
