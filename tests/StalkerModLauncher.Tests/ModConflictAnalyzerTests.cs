using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ModConflictAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnalyzeAsyncMarksModsThatOverwriteEarlierMods()
    {
        var first = CreateMod("first", "gamedata/config/shared.ltx", "gamedata/config/first.ltx");
        var second = CreateMod("second", "gamedata/config/shared.ltx");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("first", "First", first, true, []),
            new ModConflictInput("second", "Second", second, true, [])
        ]);

        Assert.False(result["first"].HasOverlapsAbove);
        Assert.True(result["second"].HasOverlapsAbove);
        Assert.Equal(ModConflictKind.Overwritten, result["first"].ConflictKind);
        Assert.Equal(ModConflictKind.Overwrite, result["second"].ConflictKind);
        Assert.Equal(1, result["first"].OverwrittenByFileCount);
        Assert.Equal(1, result["second"].OverwrittenFileCount);
        Assert.Equal(["First"], result["second"].OverwrittenModNames);
    }

    [Fact]
    public async Task AnalyzeAsyncIgnoresDisabledMods()
    {
        var first = CreateMod("first", "shared.ltx");
        var second = CreateMod("second", "shared.ltx");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("first", "First", first, true, []),
            new ModConflictInput("second", "Second", second, false, [])
        ]);

        Assert.False(result["first"].HasOverlapsAbove);
        Assert.False(result["second"].HasOverlapsAbove);
        Assert.Equal(ModConflictKind.None, result["first"].ConflictKind);
        Assert.Equal(ModConflictKind.Disabled, result["second"].ConflictKind);
    }

    [Fact]
    public async Task AnalyzeAsyncCountsUniqueOverwrittenFilesAndSourceMods()
    {
        var first = CreateMod("first", "shared-a.ltx", "shared-b.ltx");
        var second = CreateMod("second", "shared-b.ltx", "shared-c.ltx");
        var patch = CreateMod("patch", "shared-a.ltx", "shared-b.ltx", "shared-c.ltx");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("first", "Main mod", first, true, []),
            new ModConflictInput("second", "Addon", second, true, []),
            new ModConflictInput("patch", "Patch", patch, true, [])
        ]);

        Assert.Equal(3, result["patch"].OverwrittenFileCount);
        Assert.Equal(["Main mod", "Addon"], result["patch"].OverwrittenModNames);
    }

    [Fact]
    public async Task AnalyzeAsyncMarksLastEnabledProviderOfLaunchExecutable()
    {
        var main = CreateMod("main", "bin_x64/xrEngine.exe");
        var patch = CreateMod("patch", "bin_x64/xrEngine.exe");
        var disabledHotfix = CreateMod("disabled", "bin_x64/xrEngine.exe");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("main", "Main mod", main, true, []),
            new ModConflictInput("patch", "Patch", patch, true, []),
            new ModConflictInput("disabled", "Disabled hotfix", disabledHotfix, false, [])
        ], @"bin_x64\xrEngine.exe");

        Assert.False(result["main"].ProvidesLaunchExecutable);
        Assert.True(result["patch"].ProvidesLaunchExecutable);
        Assert.False(result["disabled"].ProvidesLaunchExecutable);
    }

    [Fact]
    public async Task AnalyzeAsyncUsesFileLayerPlanOrder()
    {
        var main = CreateMod("main", "gamedata/config/shared.ltx", "bin/xr_3da.exe");
        var patch = CreateMod("patch", "gamedata/config/shared.ltx", "bin/xr_3da.exe");
        var profile = new ModProfile
        {
            GameInstallPath = Path.Combine(_root, "game"),
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };
        profile.Mods.Add(new ModEntry
        {
            Id = "patch",
            Name = "Patch",
            SourcePath = patch,
            IsEnabled = true,
            Order = 2
        });
        profile.Mods.Add(new ModEntry
        {
            Id = "main",
            Name = "Main",
            SourcePath = main,
            IsEnabled = true,
            Order = 1
        });
        var plan = FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, Path.Combine(_root, "workspace"));
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(plan, profile.ExecutableRelativePath, profile.ExecutableSourcePath);

        Assert.False(result["main"].HasOverlapsAbove);
        Assert.True(result["patch"].HasOverlapsAbove);
        Assert.Equal(2, result["patch"].OverwrittenFileCount);
        Assert.False(result["main"].ProvidesLaunchExecutable);
        Assert.True(result["patch"].ProvidesLaunchExecutable);
    }

    [Fact]
    public async Task AnalyzeAsyncMarksPinnedProviderOfLaunchExecutable()
    {
        var main = CreateMod("main", "bin_x64/xrEngine.exe");
        var patch = CreateMod("patch", "bin_x64/xrEngine.exe");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("main", "Main mod", main, true, []),
            new ModConflictInput("patch", "Patch", patch, true, [])
        ], @"bin_x64\xrEngine.exe", main);

        Assert.True(result["main"].ProvidesLaunchExecutable);
        Assert.False(result["patch"].ProvidesLaunchExecutable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeAsyncDoesNotMarkModWhenExecutableIsPinnedOutsideMods(bool sourceExists)
    {
        const string relative = @"bin\xrEngine.exe";
        var mod = CreateMod("engine-mod", relative);
        var pinnedRoot = sourceExists ? CreateMod("game", relative) : Path.Combine(_root, "missing-source");

        var result = await new ModConflictAnalyzer().AnalyzeAsync(
            [new ModConflictInput("mod", "Engine mod", mod, true, [])], relative, pinnedRoot);

        Assert.False(result["mod"].ProvidesLaunchExecutable);
    }

    [Fact]
    public async Task AnalyzeAsyncMarksPinnedExecutableEvenWhenExcludedFromNormalOverlay()
    {
        const string relative = @"bin\xrEngine.exe";
        var lower = CreateMod("pinned-excluded", relative);
        var higher = CreateMod("engine-patch", relative);

        var result = await new ModConflictAnalyzer().AnalyzeAsync(
        [
            new ModConflictInput("lower", "Lower", lower, true, [relative]),
            new ModConflictInput("higher", "Higher", higher, true, [])
        ], relative, lower);

        Assert.True(result["lower"].ProvidesLaunchExecutable);
        Assert.False(result["higher"].ProvidesLaunchExecutable);
    }

    [Fact]
    public async Task AnalyzeAsyncDoesNotFallbackWhenPinnedExecutableIsMissing()
    {
        const string relative = @"bin\xrEngine.exe";
        var lower = CreateMod("pinned-missing", "shared.ltx");
        var higher = CreateMod("engine-fallback", relative);

        var result = await new ModConflictAnalyzer().AnalyzeAsync(
        [
            new ModConflictInput("lower", "Lower", lower, true, []),
            new ModConflictInput("higher", "Higher", higher, true, [])
        ], relative, lower);

        Assert.All(result.Values, state => Assert.False(state.ProvidesLaunchExecutable));
    }

    [Fact]
    public async Task AnalyzeAsyncClassifiesConfigurationAndBinaryOverlays()
    {
        var main = CreateMod("main", "gamedata/config/system.ltx", "bin/xrCore.dll", "textures/test.dds");
        var patch = CreateMod("patch", "gamedata/config/system.ltx", "bin/xrCore.dll", "textures/test.dds");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("main", "Main", main, true, []),
            new ModConflictInput("patch", "Patch", patch, true, [])
        ]);

        Assert.Equal(1, result["patch"].OverwrittenConfigurationCount);
        Assert.Equal(1, result["patch"].OverwrittenBinaryCount);
        Assert.Equal(3, result["patch"].OverwrittenFileCount);
    }

    [Fact]
    public async Task AnalyzeAsyncClassifiesMixedAndRedundantMods()
    {
        var first = CreateMod("first", "shared-a.ltx");
        var mixed = CreateMod("mixed", "shared-a.ltx", "shared-b.ltx", "unique.ltx");
        var redundant = CreateMod("redundant", "shared-c.ltx");
        var last = CreateMod("last", "shared-b.ltx", "shared-c.ltx");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("first", "First", first, true, []),
            new ModConflictInput("mixed", "Mixed", mixed, true, []),
            new ModConflictInput("redundant", "Redundant", redundant, true, []),
            new ModConflictInput("last", "Last", last, true, [])
        ]);

        Assert.Equal(ModConflictKind.Mixed, result["mixed"].ConflictKind);
        Assert.Equal(1, result["mixed"].OverwrittenFileCount);
        Assert.Equal(1, result["mixed"].OverwrittenByFileCount);
        Assert.Equal(ModConflictKind.Redundant, result["redundant"].ConflictKind);
        Assert.Equal(["last"], result["redundant"].RelatedModIds);
    }

    [Fact]
    public async Task AnalyzeAsyncIgnoresProfileExcludedFiles()
    {
        var first = CreateMod("excluded-first", "shared.ltx");
        var second = CreateMod("excluded-second", "shared.ltx");
        var analyzer = new ModConflictAnalyzer();

        var result = await analyzer.AnalyzeAsync(
        [
            new ModConflictInput("first", "First", first, true, []),
            new ModConflictInput("second", "Second", second, true, ["shared.ltx"])
        ]);

        Assert.Equal(ModConflictKind.None, result["first"].ConflictKind);
        Assert.Equal(ModConflictKind.None, result["second"].ConflictKind);
    }

    private string CreateMod(string name, params string[] relativeFiles)
    {
        var modPath = Path.Combine(_root, name);
        foreach (var relativeFile in relativeFiles)
        {
            var filePath = Path.Combine(modPath, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, name);
        }

        return modPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
