using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using System.Text.Json;
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
            Description = "Описание профиля\nSecond line",
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
        Assert.Equal(source.Description, imported.Description);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedModPathsArePortableBetweenLauncherDirectories(bool isStandalone)
    {
        Directory.CreateDirectory(_root);
        var sourcePaths = new AppPaths(Path.Combine(_root, "source"));
        var targetPaths = new AppPaths(Path.Combine(_root, "target"));
        var filePath = Path.Combine(_root, "profile.stalkerprofile");
        var relativePath = Path.Combine("Data", "CordonModLauncher", "Mods", "Большая Зона", "Fixes");
        var managedPath = Path.Combine(sourcePaths.ExecutableDirectory, relativePath);
        var externalPath = Path.Combine(sourcePaths.ConfigDirectory, "Mods-other", "External");
        var source = new ModProfile
        {
            Name = "Zona",
            IsStandalone = isStandalone,
            ExecutableSourcePath = managedPath,
            FsgameSourcePath = Path.Combine(managedPath, "config", "custom.ltx")
        };
        source.Mods.Add(new ModEntry { Name = "Managed", SourcePath = managedPath, Order = 1 });
        source.Mods.Add(new ModEntry { Name = "External", SourcePath = externalPath, Order = 2 });

        ProfileTransferService.Export(filePath, source, sourcePaths);

        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        var mods = document.RootElement.GetProperty("Mods");
        Assert.Equal(relativePath, mods[0].GetProperty("SourcePath").GetString());
        Assert.Equal(externalPath, mods[1].GetProperty("SourcePath").GetString());
        Assert.Equal(relativePath, document.RootElement.GetProperty("ExecutableSourcePath").GetString());
        Assert.Equal(Path.Combine(relativePath, "config", "custom.ltx"), document.RootElement.GetProperty("FsgameSourcePath").GetString());
        Assert.Equal(managedPath, source.Mods[0].SourcePath);
        Assert.Equal(managedPath, source.ExecutableSourcePath);

        var imported = ProfileTransferService.Import(filePath, targetPaths);

        Assert.Equal(Path.Combine(targetPaths.ExecutableDirectory, relativePath), imported.Mods[0].SourcePath);
        Assert.Equal(externalPath, imported.Mods[1].SourcePath);
        Assert.Equal(imported.Mods[0].SourcePath, imported.ExecutableSourcePath);
        Assert.Equal(Path.Combine(imported.Mods[0].SourcePath, "config", "custom.ltx"), imported.FsgameSourcePath);
        Assert.Equal(isStandalone, imported.IsStandalone);
    }

    [Fact]
    public void ImportRejectsModPathEscapingLauncherDirectory()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "unsafe-mod.stalkerprofile");
        File.WriteAllText(filePath,
            """{"Name":"Unsafe","Mods":[{"Name":"Mod","SourcePath":"..\\outside"}]}""");

        Assert.Throws<InvalidDataException>(() =>
            ProfileTransferService.Import(filePath, new AppPaths(Path.Combine(_root, "launcher"))));
    }

    [Theory]
    [InlineData(nameof(ExportedProfile.ExecutableSourcePath))]
    [InlineData(nameof(ExportedProfile.FsgameSourcePath))]
    public void ImportRejectsManualSourcePathEscapingLauncherDirectory(string property)
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "unsafe-manual-source.stalkerprofile");
        File.WriteAllText(filePath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Name"] = "Unsafe",
            [property] = @"..\outside"
        }));

        Assert.Throws<InvalidDataException>(() =>
            ProfileTransferService.Import(filePath, new AppPaths(Path.Combine(_root, "launcher"))));
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

    [Theory]
    [InlineData(LaunchBackendKind.LinkedWorkspace)]
    [InlineData(LaunchBackendKind.VirtualFileSystem)]
    public void ExportThenImportPreservesLaunchBackend(LaunchBackendKind backend)
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "vfs.stalkerprofile");
        var source = new ModProfile
        {
            Name = "VFS profile",
            LaunchBackendKind = backend,
            ExecutableRelativePath = @"bin\xrEngine.exe"
        };
        ProfileTransferService.Export(filePath, source);
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        Assert.Equal((int)backend, document.RootElement.GetProperty("LaunchBackendKind").GetInt32());
        var imported = ProfileTransferService.Import(filePath);

        Assert.Equal(backend, imported.LaunchBackendKind);
    }

    [Fact]
    public void ImportKeepsWorkspaceDefaultForLegacyProfileWithoutBackend()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "legacy.stalkerprofile");
        File.WriteAllText(filePath, """{"Name":"Legacy profile"}""");

        var imported = ProfileTransferService.Import(filePath);

        Assert.Equal(LaunchBackendKind.LinkedWorkspace, imported.LaunchBackendKind);
        Assert.Empty(imported.Description);
        Assert.Empty(imported.Mods);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("{\"Name\":null}")]
    [InlineData("{\"Name\":\"Profile\",\"Mods\":null}")]
    [InlineData("{\"Name\":\"Profile\",\"Mods\":[null]}")]
    [InlineData("{\"Name\":\"Profile\",\"Mods\":[{\"Name\":null}]}")]
    [InlineData("{\"Name\":\"Profile\",\"Mods\":[{\"SourcePath\":null}]}")]
    [InlineData("{\"Name\":\"Profile\",\"CollapsedModGroups\":[null]}")]
    [InlineData("{\"Name\":\"Profile\",\"Mods\":[{\"ExcludedFiles\":[null]}]}")]
    [InlineData("{\"Name\":\"Profile\",\"LaunchBackendKind\":42}")]
    [InlineData("{\"Name\":\"Profile\",\"LaunchBackendKind\":null}")]
    public void ImportRejectsInvalidProfileWithLocalizedError(string json)
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "invalid.stalkerprofile");
        File.WriteAllText(filePath, json);

        var error = Assert.Throws<InvalidDataException>(() => ProfileTransferService.Import(filePath));

        Assert.Equal(Strings.Profile_ImportInvalid, error.Message);
    }

    [Fact]
    public void ImportNormalizesNullOptionalSettings()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "optional.stalkerprofile");
        File.WriteAllText(filePath,
            """
            {
              "Name": "Profile",
              "Description": null,
              "ExecutableSourcePath": null,
              "FsgameSourcePath": null,
              "UsvfsExecutableOverrideRelativePath": null,
              "LaunchArguments": null,
              "WorkingDirectoryRelative": null,
              "GameInstallPath": null,
              "Mo2OverwritePath": null,
              "CollapsedModGroups": null,
              "Mods": [{"Name": "Mod", "SourcePath": "D:\\Mods\\Mod", "GroupName": null, "ExcludedFiles": null}]
            }
            """);

        var imported = ProfileTransferService.Import(filePath);

        Assert.Empty(imported.Description);
        Assert.Empty(imported.ExecutableSourcePath);
        Assert.Empty(imported.FsgameSourcePath);
        Assert.Empty(imported.UsvfsExecutableOverrideRelativePath);
        Assert.Empty(imported.LaunchArguments);
        Assert.Empty(imported.WorkingDirectoryRelative);
        Assert.Empty(imported.GameInstallPath);
        Assert.Empty(imported.Mo2OverwritePath);
        Assert.Empty(imported.CollapsedModGroups);
        var mod = Assert.Single(imported.Mods);
        Assert.Equal("Mod", mod.Name);
        Assert.Equal(@"D:\Mods\Mod", mod.SourcePath);
        Assert.Empty(mod.GroupName);
        Assert.Empty(mod.ExcludedFiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
