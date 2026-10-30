using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class OverlayManifestBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherOverlayManifestTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildVirtualFileSystemCapturesOverlayDecisions()
    {
        var game = Path.Combine(_root, "game");
        var main = Path.Combine(_root, "main");
        var patch = Path.Combine(_root, "patch");
        var workspace = Path.Combine(_root, "workspace");
        CreateFile(game, "fsgame.ltx", "base fsgame");
        CreateFile(game, "bin/xr_3da.exe", "base exe");
        CreateFile(main, "gamedata/configs/system.ltx", "main system");
        var patchFsgame = CreateFile(patch, "fsgame.ltx", "patch fsgame");
        var patchExe = CreateFile(patch, "bin_x64/xrEngine.exe", "patch exe");
        CreateFile(patch, "gamedata/configs/system.ltx", "patch system");
        var profile = new ModProfile
        {
            GameInstallPath = game,
            ExecutableRelativePath = @"bin\missing.exe",
            LaunchArguments = "-nointro"
        };
        profile.Mods.Add(new ModEntry { Id = "main", Name = "Main", SourcePath = main, Order = 1 });
        profile.Mods.Add(new ModEntry { Id = "patch", Name = "Patch", SourcePath = patch, Order = 2 });

        var plan = FileLayerPlan.CreateForProfile(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(
            profile,
            plan,
            workspace,
            includeOverwrites: true);

        Assert.Equal(4, manifest.Layers.Count);
        Assert.Equal(patchExe, manifest.Executable?.FullPath);
        Assert.Equal(patchExe, manifest.LaunchPlan?.ExecutablePath);
        Assert.Contains(
            manifest.SystemFiles,
            file => file.RelativePath == "fsgame.ltx" && file.Source?.FullPath == patchFsgame);
        Assert.Contains(
            manifest.WritableFiles,
            file => file.RelativePath == Path.Combine("gamedata", "configs", "localization.ltx") &&
                    file.StoragePath.EndsWith(
                        Path.Combine("userdata", "writable-game-files", "gamedata", "configs", "localization.ltx"),
                        StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            manifest.WritableFiles,
            file => file.RelativePath == Path.Combine("gamedata", "configs", "axr_options.ltx") &&
                    file.StoragePath.EndsWith(
                        Path.Combine("userdata", "writable-game-files", "gamedata", "configs", "axr_options.ltx"),
                        StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            manifest.SystemFiles,
            file => file.RelativePath == Path.Combine("gamedata", "configs", "axr_options.ltx"));
        Assert.EndsWith(Path.Combine("userdata", "overwrite"), manifest.WriteOverlayRoot);
        Assert.Contains(
            manifest.Overwrites,
            overwrite => overwrite.RelativePath == Path.Combine("gamedata", "configs", "system.ltx") &&
                         overwrite.ReplacedFile.SourceName == "мод: Main" &&
                         overwrite.ReplacingFile.SourceName == "мод: Patch");
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
