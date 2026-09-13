using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class FileLayerPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherLayerPlanTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateLinkedWorkspaceOrdersBaseGameEnabledModsAndUserData()
    {
        var gamePath = Path.Combine(Path.GetTempPath(), "game");
        var workspacePath = Path.Combine(Path.GetTempPath(), "workspace");
        var profile = new ModProfile { Name = "Layered profile" };
        var lowerPriorityMod = new ModEntry
        {
            Id = "lower",
            Name = "Main mod",
            SourcePath = Path.Combine(Path.GetTempPath(), "mods", "main"),
            IsEnabled = true,
            Order = 1
        };
        var higherPriorityMod = new ModEntry
        {
            Id = "higher",
            Name = "Patch",
            SourcePath = Path.Combine(Path.GetTempPath(), "mods", "patch"),
            IsEnabled = true,
            Order = 2
        };
        var disabledMod = new ModEntry
        {
            Id = "disabled",
            Name = "Disabled",
            SourcePath = Path.Combine(Path.GetTempPath(), "mods", "disabled"),
            IsEnabled = false,
            Order = 3
        };
        profile.Mods.Add(higherPriorityMod);
        profile.Mods.Add(disabledMod);
        profile.Mods.Add(lowerPriorityMod);

        var plan = FileLayerPlan.CreateLinkedWorkspace(gamePath, profile, workspacePath);

        Assert.Equal(FileLayerKind.BaseGame, plan.Layers[0].Kind);
        Assert.Equal(Path.GetFullPath(gamePath), plan.BaseGame.RootPath);
        Assert.Equal(["lower", "higher"], plan.Mods.Select(layer => layer.Id));
        Assert.Equal([lowerPriorityMod, higherPriorityMod], plan.Mods.Select(layer => layer.Mod));
        Assert.DoesNotContain(plan.Layers, layer => layer.Id == "disabled");
        Assert.Equal(FileLayerKind.UserData, plan.Layers[^1].Kind);
        Assert.Equal(Path.Combine(Path.GetFullPath(workspacePath), "userdata"), plan.UserData.RootPath);
    }

    [Fact]
    public void CreateLinkedWorkspaceRejectsStandaloneProfiles()
    {
        var profile = new ModProfile { IsStandalone = true };

        Assert.Throws<InvalidOperationException>(
            () => FileLayerPlan.CreateLinkedWorkspace("game", profile, "workspace"));
    }

    [Theory]
    [InlineData("-fsltx")]
    [InlineData(@"-fsltx ..\outside.ltx")]
    [InlineData("-fsltx config.txt")]
    public void CreateLinkedWorkspaceRejectsInvalidFsltx(string launchArguments)
    {
        var game = Path.Combine(_root, "invalid-fsltx-game");
        Directory.CreateDirectory(game);
        var profile = new ModProfile
        {
            GameInstallPath = game,
            LaunchArguments = launchArguments
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            FileLayerPlan.CreateLinkedWorkspace(game, profile, Path.Combine(_root, "invalid-fsltx-workspace")));

        Assert.Contains("-fsltx", error.Message);
    }

    [Fact]
    public void CreateLinkedWorkspaceAddsMo2OverwriteAfterModsWithoutChangingProfileModList()
    {
        var profile = new ModProfile
        {
            Mo2OverwritePath = Path.Combine(_root, "mo2-overwrite")
        };
        profile.Mods.Add(new ModEntry
        {
            Id = "regular",
            Name = "Regular",
            SourcePath = Path.Combine(_root, "regular"),
            Order = 1
        });

        var plan = FileLayerPlan.CreateLinkedWorkspace(
            Path.Combine(_root, "game"),
            profile,
            Path.Combine(_root, "workspace"));

        Assert.Equal(["regular", "__mo2_overwrite"], plan.Mods.Select(layer => layer.Id));
        Assert.Equal("Файлы overwrite из MO2", plan.Mods[^1].Name);
        Assert.Single(profile.Mods);
    }

    [Fact]
    public void FindFinalFileReturnsHighestPriorityProvider()
    {
        var game = Path.Combine(_root, "game");
        var main = Path.Combine(_root, "main");
        var patch = Path.Combine(_root, "patch");
        CreateFile(game, "gamedata/configs/system.ltx", "base");
        CreateFile(main, "gamedata/configs/system.ltx", "main");
        var patchFile = CreateFile(patch, "gamedata/configs/system.ltx", "patch");
        var profile = CreateProfile(game, main, patch);

        var plan = FileLayerPlan.CreateLinkedWorkspace(game, profile, Path.Combine(_root, "workspace"));
        var providers = plan.FindAllProviders(@"gamedata\configs\system.ltx");
        var final = plan.FindFinalFile(@"gamedata\configs\system.ltx");

        Assert.Equal(3, providers.Count);
        Assert.Equal(patchFile, final?.FullPath);
        Assert.Equal("мод: Patch", final?.SourceName);
    }

    [Fact]
    public void GetOverwrittenFilesReturnsFilesReplacedByModLayer()
    {
        var game = Path.Combine(_root, "game-overwrite");
        var main = Path.Combine(_root, "main-overwrite");
        var patch = Path.Combine(_root, "patch-overwrite");
        CreateFile(game, "gamedata/configs/system.ltx", "base");
        var mainFile = CreateFile(main, "gamedata/configs/system.ltx", "main");
        var patchFile = CreateFile(patch, "gamedata/configs/system.ltx", "patch");
        CreateFile(patch, "gamedata/configs/patch_only.ltx", "patch only");
        var profile = CreateProfile(game, main, patch);

        var plan = FileLayerPlan.CreateLinkedWorkspace(game, profile, Path.Combine(_root, "workspace-overwrite"));
        var overwrites = plan.GetOverwrittenFiles(plan.Mods.Single(layer => layer.Name == "Patch"));

        var overwrite = Assert.Single(overwrites);
        Assert.Equal(@"gamedata\configs\system.ltx", overwrite.RelativePath);
        Assert.Equal(mainFile, overwrite.ReplacedFile.FullPath);
        Assert.Equal(patchFile, overwrite.ReplacingFile.FullPath);
    }

    [Fact]
    public void GetExecutableCandidatesReturnsExecutablesByLayerOrder()
    {
        var game = Path.Combine(_root, "game-exe");
        var main = Path.Combine(_root, "main-exe");
        var patch = Path.Combine(_root, "patch-exe");
        var baseExe = CreateFile(game, "bin/xr_3da.exe", "base");
        var patchExe = CreateFile(patch, "bin_x64/xrEngine.exe", "patch");
        var profile = CreateProfile(game, main, patch);

        var plan = FileLayerPlan.CreateLinkedWorkspace(game, profile, Path.Combine(_root, "workspace-exe"));
        var candidates = plan.GetExecutableCandidates();

        Assert.Equal([baseExe, patchExe], candidates.Select(candidate => candidate.FullPath));
    }

    [Fact]
    public void FindFinalFileSkipsFilesExcludedByProfile()
    {
        var game = Path.Combine(_root, "excluded-game");
        var main = Path.Combine(_root, "excluded-main");
        var patch = Path.Combine(_root, "excluded-patch");
        CreateFile(game, "shared.ltx", "base");
        var mainFile = CreateFile(main, "shared.ltx", "main");
        CreateFile(patch, "shared.ltx", "patch");
        var profile = CreateProfile(game, main, patch);
        profile.Mods[1].ExcludedFiles.Add("shared.ltx");

        var plan = FileLayerPlan.CreateLinkedWorkspace(game, profile, Path.Combine(_root, "excluded-workspace"));

        Assert.Equal(mainFile, plan.FindFinalFile("shared.ltx")?.FullPath);
    }

    private static ModProfile CreateProfile(string game, string main, string patch)
    {
        var profile = new ModProfile { GameInstallPath = game };
        profile.Mods.Add(new ModEntry { Id = "main", Name = "Main", SourcePath = main, Order = 1 });
        profile.Mods.Add(new ModEntry { Id = "patch", Name = "Patch", SourcePath = patch, Order = 2 });
        return profile;
    }

    private static string CreateFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
