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
