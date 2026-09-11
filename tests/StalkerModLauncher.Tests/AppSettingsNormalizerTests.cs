using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class AppSettingsNormalizerTests
{
    [Fact]
    public void NormalizeResetsUnknownLaunchBackendToLinkedWorkspace()
    {
        var profile = new ModProfile { LaunchBackendKind = (LaunchBackendKind)999 };
        var settings = new AppSettings { Profiles = [profile] };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(LaunchBackendKind.LinkedWorkspace, normalized.Profiles[0].LaunchBackendKind);
    }

    [Fact]
    public void NormalizePreservesVirtualFileSystemSelectionWithoutRuntime()
    {
        var profile = new ModProfile
        {
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem
        };
        var settings = new AppSettings { Profiles = [profile] };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(LaunchBackendKind.VirtualFileSystem, normalized.Profiles[0].LaunchBackendKind);
    }

    [Fact]
    public void NormalizeClearsUnsupportedAnomalyUsvfsOverride()
    {
        var profile = new ModProfile
        {
            UsvfsExecutableOverrideRelativePath = @"bin\Unknown.exe"
        };
        var settings = new AppSettings { Profiles = [profile] };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Empty(normalized.Profiles[0].UsvfsExecutableOverrideRelativePath);
    }

    [Fact]
    public void NormalizePreservesPdaInterfacePreference()
    {
        var settings = new AppSettings
        {
            IsPdaInterfaceEnabled = true,
            UseNewPdaInterface = true
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.True(normalized.IsPdaInterfaceEnabled);
        Assert.True(normalized.UseNewPdaInterface);
    }

    [Fact]
    public void NormalizeRepairsUnknownLauncherLogLevel()
    {
        var settings = new AppSettings { LogLevel = (LauncherLogLevel)999 };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(LauncherLogLevel.Standard, normalized.LogLevel);
    }

    [Fact]
    public void NormalizeDisablesTrayOnlyBehaviorWhenTrayIconIsHidden()
    {
        var settings = new AppSettings
        {
            ShowTrayIcon = false,
            StartMinimizedToTrayOnWindowsStartup = true,
            MinimizeToTrayOnClose = true
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.False(normalized.StartMinimizedToTrayOnWindowsStartup);
        Assert.False(normalized.MinimizeToTrayOnClose);
    }

}
