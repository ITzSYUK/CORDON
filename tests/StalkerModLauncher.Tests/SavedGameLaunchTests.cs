using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class SavedGameLaunchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cordon-saved-game-").FullName;

    [Theory]
    [InlineData("AnomalyLauncher.exe")]
    [InlineData(@"bin\AnomalyDX11.exe")]
    public void AnomalyCommandLineFileRemainsEngineOwned(string exe)
    {
        Write("AnomalyLauncher.exe", "launcher");
        var engine = Write(@"bin\AnomalyDX11.exe", "engine");
        Write("AnomalyLauncher.cfg", "DX11\nNOAVX\n");
        Write("fsgame.ltx", "$game_saves$ = true | false | $fs_root$ | slots");
        var save = Write(@"slots\chosen.scop", "save");
        var commandLine = Write("commandline.txt", "-smap2048\n-prefetch_sounds");
        var plan = new LaunchPlan(LaunchBackendKind.Standalone, Path.Combine(_root, exe), "-dbg", _root);

        var result = SavedGameLaunch.ApplyToPhysicalPlan(plan, save);

        Assert.Equal(engine, result.ExecutablePath);
        Assert.Equal("-dbg -start server(chosen/single/alife/load) client(localhost)", result.Arguments);
        Assert.Equal("-smap2048\n-prefetch_sounds", File.ReadAllText(commandLine));
        File.AppendAllText(commandLine, "\n-load other");
        Assert.Throws<InvalidOperationException>(() => SavedGameLaunch.ApplyToPhysicalPlan(plan, save));
    }

    [Theory]
    [InlineData("swm_launcher.exe")]
    [InlineData("host.exe")]
    [InlineData("renamed.exe")]
    public void WrapperDoesNotInheritGameModulesDirectLoadCapability(string name)
    {
        var executable = Write(Path.Combine("bin", name), "launcher");
        Write(@"bin\xrGame.dll", "\0.scop\0");
        Write("fsgame.ltx", "$game_saves$ = true | false | $fs_root$ | slots");
        var save = Write(@"slots\chosen.scop", "save");
        var plan = new LaunchPlan(LaunchBackendKind.Standalone, executable, "", _root);

        var catalog = SavedGameCatalog.ForPhysicalPlan(plan, executable);

        Assert.Null(catalog.Extension);
        Assert.Equal(save, Assert.Single(catalog.Enumerate()).FullName);
        Assert.Throws<InvalidOperationException>(() => SavedGameLaunch.ApplyToPhysicalPlan(plan, save));
    }

    [Fact]
    public void StandaloneUsesAbsoluteFsgameArgumentAndDoesNotBlockByExeName()
    {
        var executable = Write(@"bin\swm_launcher.exe", "engine\0.sav\0");
        var config = Write(@"config\alternate.ltx", "$game_saves$ = true | false | $fs_root$ | slots");
        var save = Write(@"slots\chosen.sav", "save");
        var plan = new LaunchPlan(LaunchBackendKind.Standalone, executable, $"-fsltx \"{config}\"", _root);

        var result = SavedGameLaunch.ApplyToPhysicalPlan(plan, save);

        Assert.Equal(executable, result.ExecutablePath);
        Assert.Equal($"{plan.Arguments} -start server(chosen/single/alife/load) client(localhost)", result.Arguments);
    }

    [Theory]
    [InlineData("-cs", "cop", ".sav")]
    [InlineData("-cop", "soc", ".scop")]
    [InlineData("-unlock_game_mode", "soc", ".scop")]
    [InlineData("", "cs", ".sav")]
    [InlineData("", "cop", ".scop")]
    public void MixedFormatModuleUsesSelectedGameMode(string arguments, string configuredMode, string extension)
    {
        var exe = Write(@"bin\xrEngine.exe", "\0OpenXRay\0xrGame.dll\0");
        Write(@"bin\xrGame.dll", "\0.sav\0.scop\0");
        Write("fsgame.ltx", """
            $game_config$ = true | false | $fs_root$ | configs
            $game_saves$ = true | false | $fs_root$ | slots
            """);
        Write(@"configs\openxray.ltx", $"[compatibility]\ngame_mode = {configuredMode}");
        Write("openxray.ltx", "[compatibility]\ngame_mode = cop");
        var plan = new LaunchPlan(LaunchBackendKind.Standalone, exe, arguments, _root);

        Assert.Equal(extension, SavedGameCatalog.ForPhysicalPlan(plan, exe).Extension);
    }

    [Fact]
    public void VirtualSaveLookupRequiresTheEffectiveFileFromTheActiveLayers()
    {
        var exe = Write(@"game\bin\xrEngine.exe", "engine\0.scop\0");
        Write(@"game\fsgame.ltx", """
            $app_data_root$ = true | false | $fs_root$ | appdata
            $game_saves$ = true | false | $fs_root$ | slots
            """);
        var game = Path.Combine(_root, "game");
        var work = Path.Combine(_root, "workspace");
        var baseSave = Write(@"game\slots\same.scop", "base save");
        var replaced = Write(@"mod\slots\same.scop", "mod save");
        var privateSave = Write(@"workspace\userdata\overwrite\slots\same.scop", "profile save");
        var profile = new ModProfile { GameInstallPath = game };
        profile.Mods.Add(new ModEntry { SourcePath = Path.Combine(_root, "mod") });
        var layers = FileLayerPlan.CreateForProfile(game, profile, work);
        var catalog = SavedGameCatalog.ForVirtualTarget(layers,
            new UsvfsLaunchTarget(exe, @"bin\xrEngine.exe", "", game, "base", false));

        Assert.Equal(privateSave, Assert.Single(catalog.Enumerate()).FullName);
        Assert.Equal("same", SavedGameLaunch.GetSaveName(catalog, privateSave));
        Assert.Throws<FileNotFoundException>(() => SavedGameLaunch.GetSaveName(catalog, replaced));
        Assert.Throws<FileNotFoundException>(() => SavedGameLaunch.GetSaveName(catalog, baseSave));
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
