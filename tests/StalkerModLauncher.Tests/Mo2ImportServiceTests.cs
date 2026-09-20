using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class Mo2ImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverAndPreviewImportsFoldersOrderStateGroupsAndOverwrite()
    {
        var source = CreatePortableMo2(
            "+High",
            "+Gameplay_separator",
            "-Low",
            "+Missing");
        CreateMod(source.ModsPath, "High", @"gamedata\configs\high.ltx");
        CreateMod(source.ModsPath, "Low", @"gamedata\configs\low.ltx");
        CreateFile(Path.Combine(source.OverwritePath, "gamedata", "configs", "generated.ltx"));

        var discovery = Mo2ImportService.Discover(source.RootPath);
        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);

        Assert.Equal(source.GamePath, discovery.GamePath);
        Assert.Equal(2, preview.FoundModCount);
        Assert.Equal(1, preview.EnabledModCount);
        Assert.Equal(1, preview.MissingModCount);
        Assert.Equal(1, preview.SeparatorCount);
        Assert.True(preview.HasOverwriteContent);
        Assert.Equal(["Missing", "Low", "High"], preview.Entries.Select(entry => entry.Name));
        Assert.Equal("Gameplay", preview.Entries.Single(entry => entry.Name == "High").GroupName);
        Assert.Equal(string.Empty, preview.Entries.Single(entry => entry.Name == "Low").GroupName);

        var profile = Mo2ImportService.CreateProfile(preview, "Imported", includeOverwrite: true);

        Assert.Equal("Imported", profile.Name);
        Assert.Equal($"Импортировано из Mod Organizer 2: {preview.Profile.Name}", profile.Description);
        Assert.Equal(source.GamePath, profile.GameInstallPath);
        Assert.Equal(["Low", "High"], profile.Mods.Select(mod => mod.Name));
        Assert.Equal(source.OverwritePath, profile.Mo2OverwritePath);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, profile.LaunchBackendKind);
        Assert.False(profile.Mods[0].IsEnabled);
        Assert.Equal(string.Empty, profile.Mods[0].GroupName);
        Assert.Equal("Gameplay", profile.Mods[1].GroupName);
        Assert.Equal([1, 2], profile.Mods.Select(mod => mod.Order));
        Assert.Equal(@"bin\xr_3da.exe", profile.ExecutableRelativePath);
        Assert.Empty(profile.LaunchArguments);
    }

    [Fact]
    public void CreatePreviewAssignsSeparatorToModsBelowItInMo2Interface()
    {
        var source = CreatePortableMo2(
            "+GroupedThree",
            "+GroupedTwo",
            "+GroupedOne",
            "-тест_separator",
            "+Ungrouped");
        CreateMod(source.ModsPath, "GroupedOne", @"gamedata\one.txt");
        CreateMod(source.ModsPath, "GroupedTwo", @"gamedata\two.txt");
        CreateMod(source.ModsPath, "GroupedThree", @"gamedata\three.txt");
        CreateMod(source.ModsPath, "Ungrouped", @"gamedata\ungrouped.txt");

        var discovery = Mo2ImportService.Discover(source.RootPath);
        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);

        Assert.Equal("тест", preview.Entries.Single(entry => entry.Name == "GroupedOne").GroupName);
        Assert.Equal("тест", preview.Entries.Single(entry => entry.Name == "GroupedTwo").GroupName);
        Assert.Equal("тест", preview.Entries.Single(entry => entry.Name == "GroupedThree").GroupName);
        Assert.Equal(string.Empty, preview.Entries.Single(entry => entry.Name == "Ungrouped").GroupName);
    }

    [Fact]
    public void DiscoverFromModListFindsSiblingProfilesAndConfiguredPaths()
    {
        var source = CreatePortableMo2("+One");
        var secondProfile = Directory.CreateDirectory(Path.Combine(source.RootPath, "profiles", "Second"));
        File.WriteAllText(Path.Combine(secondProfile.FullName, "modlist.txt"), "+Two");

        var discovery = Mo2ImportService.Discover(source.ModListPath);

        Assert.Equal(2, discovery.Profiles.Count);
        Assert.Equal("Default", discovery.SelectedProfile?.Name);
        Assert.Equal(source.ModsPath, discovery.ModsPath);
        Assert.Equal(source.OverwritePath, discovery.OverwritePath);
    }

    [Fact]
    public void PreviewReportsAmbiguousNormalizedFolderMatches()
    {
        var source = CreatePortableMo2("+Foo_1");
        Directory.CreateDirectory(Path.Combine(source.ModsPath, "Foo-1"));
        Directory.CreateDirectory(Path.Combine(source.ModsPath, "Foo 1"));
        var discovery = Mo2ImportService.Discover(source.RootPath);

        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);

        var entry = Assert.Single(preview.Entries);
        Assert.True(entry.IsAmbiguous);
        Assert.Equal(2, entry.CandidatePaths.Count);
        Assert.Equal(1, preview.AmbiguousModCount);
    }

    [Fact]
    public void PreviewAllowsResolvingAmbiguousFolderBeforeProfileCreation()
    {
        var source = CreatePortableMo2("+Foo_1");
        var firstCandidate = Directory.CreateDirectory(Path.Combine(source.ModsPath, "Foo-1")).FullName;
        var secondCandidate = Directory.CreateDirectory(Path.Combine(source.ModsPath, "Foo 1")).FullName;
        var discovery = Mo2ImportService.Discover(source.RootPath);
        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);
        var entry = Assert.Single(preview.Entries);

        Assert.Throws<InvalidOperationException>(() =>
            Mo2ImportService.CreateProfile(preview, "Unresolved", includeOverwrite: false));

        entry.SourcePath = secondCandidate;

        Assert.False(entry.IsAmbiguous);
        Assert.True(entry.IsAvailable);
        Assert.Equal("Выбрано вручную", entry.Status);
        Assert.Equal(0, preview.AmbiguousModCount);
        Assert.Equal(1, preview.FoundModCount);
        var profile = Mo2ImportService.CreateProfile(preview, "Resolved", includeOverwrite: false);
        Assert.Equal(secondCandidate, Assert.Single(profile.Mods).SourcePath);
        Assert.NotEqual(firstCandidate, profile.Mods[0].SourcePath);
    }

    [Fact]
    public void PreviewAllowsSelectingFolderForMissingMod()
    {
        var source = CreatePortableMo2("+Missing");
        var manualPath = Directory.CreateDirectory(Path.Combine(_root, "ManualMod")).FullName;
        var discovery = Mo2ImportService.Discover(source.RootPath);
        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);
        var entry = Assert.Single(preview.Entries);

        Assert.True(entry.CanBrowseSource);
        entry.SourcePath = manualPath;

        Assert.True(entry.IsAvailable);
        Assert.False(entry.CanBrowseSource);
        Assert.Equal("Выбрано вручную", entry.Status);
        var profile = Mo2ImportService.CreateProfile(
            preview,
            "Imported",
            includeOverwrite: false);
        Assert.Equal(manualPath, Assert.Single(profile.Mods).SourcePath);
    }

    [Fact]
    public void CreateProfileCanExcludeOverwrite()
    {
        var source = CreatePortableMo2("+One");
        CreateMod(source.ModsPath, "One", @"gamedata\one.txt");
        CreateFile(Path.Combine(source.OverwritePath, "generated.txt"));
        var discovery = Mo2ImportService.Discover(source.RootPath);
        var preview = Mo2ImportService.CreatePreview(
            discovery,
            Assert.Single(discovery.Profiles),
            discovery.GamePath,
            discovery.ModsPath,
            discovery.OverwritePath);

        var profile = Mo2ImportService.CreateProfile(
            preview,
            "Without overwrite",
            includeOverwrite: false,
            LaunchBackendKind.LinkedWorkspace);

        Assert.Equal(["One"], profile.Mods.Select(mod => mod.Name));
        Assert.Empty(profile.Mo2OverwritePath);
        Assert.Equal(LaunchBackendKind.LinkedWorkspace, profile.LaunchBackendKind);
    }

    private Mo2Source CreatePortableMo2(params string[] modListLines)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "MO2")).FullName;
        var game = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        CreateFile(Path.Combine(game, "bin", "xr_3da.exe"));
        var mods = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        var overwrite = Directory.CreateDirectory(Path.Combine(root, "overwrite")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(root, "profiles", "Default")).FullName;
        var modList = Path.Combine(profile, "modlist.txt");
        File.WriteAllLines(modList, modListLines);
        File.WriteAllLines(Path.Combine(root, "ModOrganizer.ini"),
        [
            "[General]",
            $"gamePath={game.Replace("\\", "\\\\", StringComparison.Ordinal)}",
            "selected_profile=@ByteArray(Default)",
            "[Settings]",
            $"base_directory={root.Replace("\\", "\\\\", StringComparison.Ordinal)}",
            "mods_directory=mods",
            "profiles_directory=profiles",
            "overwrite_directory=overwrite"
        ]);
        return new Mo2Source(root, game, mods, overwrite, modList);
    }

    private static void CreateMod(string modsPath, string name, string relativeFile)
    {
        var path = Directory.CreateDirectory(Path.Combine(modsPath, name)).FullName;
        CreateFile(Path.Combine(path, relativeFile));
    }

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Mo2Source(
        string RootPath,
        string GamePath,
        string ModsPath,
        string OverwritePath,
        string ModListPath);
}
