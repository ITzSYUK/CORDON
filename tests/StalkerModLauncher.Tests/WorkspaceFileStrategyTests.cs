using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class WorkspaceFileStrategyTests
{
    [Fact]
    public void MustCopyReturnsTrueForFsgameThatLauncherRewrites()
    {
        Assert.True(WorkspaceFileStrategy.MustCopy("fsgame.ltx"));
    }

    [Fact]
    public void MustCopyReturnsTrueForCrossVolumeXRayScripts()
    {
        Assert.True(WorkspaceFileStrategy.MustCopy(
            Path.Combine("gamedata", "scripts", "callbacks_gameobject.script"),
            @"E:\mods\callbacks_gameobject.script",
            @"D:\workspace\gamedata\scripts\callbacks_gameobject.script"));
    }

    [Fact]
    public void MustCopyReturnsFalseForSameVolumeXRayScripts()
    {
        Assert.False(WorkspaceFileStrategy.MustCopy(
            Path.Combine("gamedata", "scripts", "callbacks_gameobject.script"),
            @"D:\mods\callbacks_gameobject.script",
            @"D:\workspace\gamedata\scripts\callbacks_gameobject.script"));
    }

    [Fact]
    public void LinkProbeSkipsCrossVolumeFilesThatAreCopiedOrExcluded()
    {
        var fixture = Directory.CreateTempSubdirectory("CordonLinkProbe-").FullName;
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(fixture, "game")).FullName;
            var mod = Directory.CreateDirectory(Path.Combine(fixture, "mod")).FullName;
            var workspace = Path.Combine(fixture, "workspace");
            // Use the device path spelling so the test requires only one physical volume.
            var otherRootWorkspace = @"\\?\" + workspace;
            var profile = new ModProfile
            {
                GameInstallPath = game,
                LaunchBackendKind = LaunchBackendKind.LinkedWorkspace,
                Mods = [new ModEntry { SourcePath = mod, ExcludedFiles = ["excluded.txt"], Order = 1 }]
            };
            File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
            var scripts = Directory.CreateDirectory(Path.Combine(mod, "gamedata", "scripts")).FullName;
            File.WriteAllText(Path.Combine(scripts, "test.script"), "copy");
            File.WriteAllText(Path.Combine(mod, "excluded.txt"), "excluded");
            var readOnly = Path.Combine(mod, "read-only.txt");
            File.WriteAllText(readOnly, "copy if symlinks are unavailable");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var plan = FileLayerPlan.CreateForProfile(game, profile, workspace);
            Assert.Empty(WorkspaceMaterializer.FindCrossVolumeLinkSources(plan, otherRootWorkspace, null, default));
            var snapshot = WorkspaceSourceScanner.Capture(plan, default);
            Assert.Empty(WorkspaceMaterializer.FindCrossVolumeLinkSources(plan, otherRootWorkspace, snapshot, default));

            var requiredLink = Path.Combine(mod, "required.txt");
            File.WriteAllText(requiredLink, "requires a link");
            Assert.Equal([requiredLink], WorkspaceMaterializer.FindCrossVolumeLinkSources(plan, otherRootWorkspace, null, default));
            snapshot = WorkspaceSourceScanner.Capture(plan, default);
            Assert.Equal([requiredLink], WorkspaceMaterializer.FindCrossVolumeLinkSources(plan, otherRootWorkspace, snapshot, default));
        }
        finally
        {
            var readOnly = Path.Combine(fixture, "mod", "read-only.txt");
            if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal);
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Theory]
    [InlineData("gamedata.db0")]
    [InlineData("gamedata/textures/texture.dds")]
    [InlineData("gamedata/config/system.ltx")]
    [InlineData("gamedata/scripts/test.script")]
    [InlineData("gamedata/config/localization.xml")]
    [InlineData("appdata/user.ltx")]
    [InlineData("userdata/logs/xray.log")]
    [InlineData("bin/xr_3da.exe")]
    [InlineData("bin/xrCore.dll")]
    public void MustCopyReturnsFalseForFilesThatCanBeLinked(string path)
    {
        Assert.False(WorkspaceFileStrategy.MustCopy(path));
    }
}
