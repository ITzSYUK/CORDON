using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileTransferServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportThenImportPreservesPortableProfileSettings()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "profile.stalkerprofile");
        var source = new ModProfile
        {
            Name = "Zona",
            IsStandalone = true,
            IsDiscordStatusEnabled = false,
            ExecutableRelativePath = @"bin_x64\xrEngine.exe",
            FsgameSourcePath = @"D:\Mods\Zona\fsgame.ltx",
            LaunchArguments = "-nointro",
            UsvfsExecutableOverrideRelativePath = @"bin\AnomalyDX9AVX.exe",
            GameInstallPath = @"D:\Games\Stalker",
            WorkspacePath = @"D:\Workspaces\Zona",
            CollapsedModGroups = ["Gameplay"],
            IsRunning = true,
            TotalPlaytimeSeconds = 3600,
            LastPlayedAt = DateTime.Now
        };
        source.Mods.Add(new ModEntry
        {
            Name = "Main",
            SourcePath = @"D:\Mods\Zona",
            GroupName = "Gameplay",
            IsEnabled = true,
            Order = 1
        });
        ProfileTransferService.Export(filePath, source);
        var imported = ProfileTransferService.Import(filePath);

        Assert.Equal(source.Name, imported.Name);
        Assert.False(imported.IsDiscordStatusEnabled);
        Assert.Equal(source.ExecutableRelativePath, imported.ExecutableRelativePath);
        Assert.Equal(source.FsgameSourcePath, imported.FsgameSourcePath);
        Assert.Equal(source.UsvfsExecutableOverrideRelativePath, imported.UsvfsExecutableOverrideRelativePath);
        Assert.Equal(source.Mods[0].SourcePath, imported.Mods[0].SourcePath);
        Assert.Equal("Gameplay", imported.Mods[0].GroupName);
        Assert.Equal(["Gameplay"], imported.CollapsedModGroups);
        Assert.Equal(1, imported.Mods[0].Order);
        Assert.NotEqual(source.Id, imported.Id);
        Assert.Empty(imported.WorkspacePath);
        Assert.False(imported.IsRunning);
        Assert.Equal(0, imported.TotalPlaytimeSeconds);
        Assert.Null(imported.LastPlayedAt);
    }

    [Fact]
    public void ImportRejectsUnsafeExecutablePath()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "unsafe.stalkerprofile");
        File.WriteAllText(
            filePath,
            """
            {
              "name": "Unsafe",
              "executableRelativePath": "..\\outside.exe",
              "mods": []
            }
            """);
        Assert.Throws<InvalidDataException>(() => ProfileTransferService.Import(filePath));
    }

    [Fact]
    public void ExportThenImportNormalizesLegacyVirtualFileSystemBackend()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "vfs.stalkerprofile");
        var source = new ModProfile
        {
            Name = "VFS profile",
            LaunchBackendKind = LaunchBackendKind.VirtualFileSystem,
            ExecutableRelativePath = @"bin\xrEngine.exe"
        };
        ProfileTransferService.Export(filePath, source);
        var imported = ProfileTransferService.Import(filePath);

        Assert.Equal(LaunchBackendKind.LinkedWorkspace, imported.LaunchBackendKind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
