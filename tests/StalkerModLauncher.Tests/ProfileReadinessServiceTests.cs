using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileReadinessServiceTests : IDisposable
{
    private const string ValidFsgame = "$app_data_root$ = true | false | $fs_root$ | appdata";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateAcceptsConfiguredOverlayProfile()
    {
        CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game") };

        var result = ProfileReadinessService.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal("Готов к запуску.", result.Summary);
    }

    [Fact]
    public void ValidateRejectsAutomaticFsgameWithoutAppDataRoot()
    {
        CreateFile("automatic-invalid/fsgame.ltx");
        CreateFile("automatic-invalid/bin/xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "automatic-invalid") };

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("$app_data_root$", result.Summary);
    }

    [Fact]
    public void ValidateUsesNamedFsgameFromFsltx()
    {
        CreateFile("fsltx/fsgame_coc.ltx", ValidFsgame);
        CreateFile("fsltx/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.Combine(_root, "fsltx"),
            LaunchArguments = "-fsltx fsgame_coc.ltx"
        };

        var result = ProfileReadinessService.Validate(profile);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateAcceptsManualFsgameAndExecutableFromEnabledMod()
    {
        var fsgame = CreateFile(
            "coc/fsgame_coc.ltx",
            "$app_data_root$ = true | false | $fs_root$ | _appdata_\\");
        CreateFile("coc-mod/_bin/xrEngine.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.Combine(_root, "coc"),
            ExecutableRelativePath = @"_bin\xrEngine.exe",
            FsgameSourcePath = fsgame
        };
        profile.Mods.Add(new ModEntry
        {
            Name = "CoC engine",
            SourcePath = Path.Combine(_root, "coc-mod"),
            Order = 1
        });

        var result = ProfileReadinessService.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal("Готов к запуску.", result.Summary);
    }

    [Fact]
    public void ValidateRejectsManualFsgameWithoutAppDataRoot()
    {
        var fsgame = CreateFile("invalid-fsgame/fsgame_coc.ltx");
        CreateFile("invalid-fsgame/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.Combine(_root, "invalid-fsgame"),
            FsgameSourcePath = fsgame
        };

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("$app_data_root$", result.Summary);
    }

    [Fact]
    public void ValidateRejectsOverlayProfileWithoutOwnGamePath()
    {
        CreateFile("default-game/fsgame.ltx", ValidFsgame);
        CreateFile("default-game/bin/xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = string.Empty };

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("Выберите папку с установленной игрой.", result.Summary);
    }

    [Fact]
    public void ValidateRejectsMissingEnabledMod()
    {
        CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game") };
        profile.Mods.Add(new ModEntry { Name = "Missing", SourcePath = Path.Combine(_root, "missing") });

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("Папка мода не найдена: Missing", result.Summary);
    }

    [Fact]
    public void ValidateRejectsMissingMo2OverwriteLayer()
    {
        CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.Combine(_root, "game"),
            Mo2OverwritePath = Path.Combine(_root, "missing-overwrite")
        };

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("Папка MO2 overwrite не найдена", result.Summary);
    }

    [Fact]
    public void ValidateRequiresExactlyOneStandaloneModAndSafeExecutable()
    {
        var modPath = Path.Combine(_root, "mod");
        Directory.CreateDirectory(modPath);
        var profile = new ModProfile { IsStandalone = true, ExecutableRelativePath = @"..\outside.exe" };
        profile.Mods.Add(new ModEntry { Name = "Standalone", SourcePath = modPath });

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("не должен выходить", result.Summary);
    }

    [Fact]
    public void ValidateRejectsExcludedUniqueFileWithoutFallbackProvider()
    {
        CreateFile("game/fsgame.ltx", ValidFsgame);
        CreateFile("game/bin/xr_3da.exe");
        CreateFile("mod/unique.ltx");
        var profile = new ModProfile { GameInstallPath = Path.Combine(_root, "game") };
        profile.Mods.Add(new ModEntry
        {
            Name = "Mod",
            SourcePath = Path.Combine(_root, "mod"),
            ExcludedFiles = ["unique.ltx"]
        });

        var result = ProfileReadinessService.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains("больше не имеет другого поставщика", result.Summary);
    }

    [Fact]
    public void ValidateUsesCommonReadySummaryForStandaloneProfile()
    {
        var modPath = Path.Combine(_root, "standalone");
        Directory.CreateDirectory(modPath);
        var profile = new ModProfile { IsStandalone = true };
        profile.Mods.Add(new ModEntry { Name = "Standalone", SourcePath = modPath });

        var result = ProfileReadinessService.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal("Готов к запуску.", result.Summary);
    }

    private string CreateFile(string relativePath, string contents = "")
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
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
