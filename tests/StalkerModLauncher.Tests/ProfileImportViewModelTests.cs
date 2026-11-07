using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileImportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviewHonorsPinnedExecutableInsteadOfHigherPriorityMod()
    {
        var game = CreateExecutable("game");
        var mod = CreateExecutable("mod");
        var profile = new ModProfile { Name = "Pinned", GameInstallPath = game, ExecutableSourcePath = game };
        profile.Mods.Add(new ModEntry { Name = "Higher", SourcePath = mod, Order = 1 });
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));

        await model.PreviewRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LocalizedText.Format(Strings.Executable_SummaryFormat,
            profile.ExecutableRelativePath, Strings.LaunchPlan_SelectedManually, Strings.Layer_BaseGame), model.ExecutableSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewUsesLayerExclusionsAndMo2Overwrite(bool includeOverwrite)
    {
        var profile = new ModProfile
        {
            Name = "Layers",
            GameInstallPath = CreateExecutable("game"),
            Mo2OverwritePath = includeOverwrite ? CreateExecutable("overwrite") : string.Empty
        };
        profile.Mods.Add(new ModEntry
        {
            Name = "Excluded",
            SourcePath = CreateExecutable("mod"),
            Order = 1,
            ExcludedFiles = [profile.ExecutableRelativePath]
        });
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));

        await model.PreviewRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        var sourceName = includeOverwrite
            ? LocalizedText.Format(Strings.Layer_ModFormat, Strings.Layer_Mo2OverwriteFiles)
            : Strings.Layer_BaseGame;
        Assert.Equal(LocalizedText.Format(Strings.Executable_SummaryFormat,
            profile.ExecutableRelativePath, Strings.LaunchPlan_SelectedPathFound, sourceName), model.ExecutableSummary);
    }

    [Fact]
    public async Task StandalonePreviewUsesFirstEnabledBuildAndIgnoresOverlayExclusions()
    {
        var profile = new ModProfile { Name = "Standalone", IsStandalone = true, ExecutableRelativePath = "missing.exe" };
        profile.Mods.Add(new ModEntry
        {
            Name = "First",
            SourcePath = CreateExecutable("first"),
            Order = 1,
            ExcludedFiles = [@"bin\xr_3da.exe"]
        });
        profile.Mods.Add(new ModEntry { Name = "Second", SourcePath = CreateExecutable("second", @"bin_x64\xrEngine.exe"), Order = 2 });
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));

        await model.PreviewRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(@"bin\xr_3da.exe", model.ExecutableSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Strings.Profile_Standalone, model.ExecutableSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("xrEngine", model.ExecutableSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelinkingSourceRebasesManualPathsAndSupportsRetry(bool relocateGame)
    {
        var oldRoot = Path.Combine(_root, "old");
        var firstRoot = CreateExecutable("first");
        var finalRoot = CreateExecutable("final");
        const string config = @"config\custom.ltx";
        Directory.CreateDirectory(Path.Combine(finalRoot, "config"));
        File.WriteAllText(Path.Combine(finalRoot, config), "$app_data_root$ = true| false| $fs_root$| appdata\\");
        var profile = new ModProfile
        {
            Name = "Relink",
            GameInstallPath = relocateGame ? oldRoot : CreateExecutable("game"),
            ExecutableSourcePath = oldRoot,
            FsgameSourcePath = Path.Combine(oldRoot, config)
        };
        var mod = new ModEntry { Name = "Mod", SourcePath = relocateGame ? CreateExecutable("mod") : oldRoot, Order = 1 };
        profile.Mods.Add(mod);
        var attempts = 0;
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(++attempts > 1));
        if (relocateGame) model.GamePath = firstRoot;
        else model.Entries[0].SourcePath = firstRoot;
        Assert.Equal(oldRoot, profile.ExecutableSourcePath);
        Assert.Equal(Path.Combine(oldRoot, config), profile.FsgameSourcePath);
        model.ImportCommand.Execute(null);
        Assert.Equal(1, attempts);

        if (relocateGame) model.GamePath = finalRoot;
        else model.Entries[0].SourcePath = finalRoot;
        await model.PreviewRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        model.ImportCommand.Execute(null);

        Assert.Equal(2, attempts);
        Assert.Same(mod, Assert.Single(profile.Mods));
        Assert.Equal(finalRoot, profile.ExecutableSourcePath);
        Assert.Equal(Path.Combine(finalRoot, config), profile.FsgameSourcePath);
        Assert.Contains(Strings.LaunchPlan_SelectedManually, model.ExecutableSummary, StringComparison.Ordinal);
        Assert.Equal(profile.FsgameSourcePath, FileLayerPlan.ResolveFsgameSource(profile)!.FullPath);
    }

    [Fact]
    public async Task SupersededAndDisposedPreviewsCannotPublishOldResults()
    {
        var first = CreateExecutable("first");
        var final = CreateExecutable("final", @"bin\xrEngine.exe");
        var profile = new ModProfile { Name = "Async", GameInstallPath = first, ExecutableRelativePath = "missing.exe" };
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));
        var oldRefresh = model.PreviewRefresh;
        model.GamePath = final;
        var latestRefresh = model.PreviewRefresh;
        Assert.False(latestRefresh.IsCompleted);
        Assert.Empty(model.ExecutableSummary);

        await Task.WhenAll(oldRefresh, latestRefresh).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(@"bin\xrEngine.exe", model.ExecutableSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xr_3da", model.ExecutableSummary, StringComparison.OrdinalIgnoreCase);
        model.GamePath = first;
        var disposedRefresh = model.PreviewRefresh;
        model.Dispose();
        await disposedRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(model.ExecutableSummary);
    }

    private string CreateExecutable(string folder, string relativePath = @"bin\xr_3da.exe")
    {
        var root = Path.Combine(_root, folder);
        var executable = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        return root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DraftChangesAreAppliedOnlyOnConfirmationAndKeepModSettings()
    {
        var profile = new ModProfile { Name = "Original", GameInstallPath = @"C:\OldGame" };
        var mod = new ModEntry { Name = "Mod", SourcePath = @"C:\Mods\Mod", IsEnabled = false, ExcludedFiles = ["file.ltx"] };
        profile.Mods.Add(mod);
        ModProfile? committed = null;
        var completed = false;
        using var model = new ProfileImportViewModel(profile, imported =>
        {
            committed = imported;
            return Task.FromResult(true);
        });
        model.Completed += (_, _) => completed = true;
        model.ProfileName = "  Renamed  ";
        model.GamePath = @"D:\NewGame";

        Assert.Null(committed);
        Assert.Equal("Original", profile.Name);
        Assert.Equal(@"C:\OldGame", profile.GameInstallPath);
        model.ImportCommand.Execute(null);

        Assert.True(completed);
        Assert.Same(profile, committed);
        Assert.Equal("Renamed", profile.Name);
        Assert.Equal(@"D:\NewGame", profile.GameInstallPath);
        Assert.Same(mod, Assert.Single(profile.Mods));
        Assert.False(mod.IsEnabled);
        Assert.Equal(["file.ltx"], mod.ExcludedFiles);
    }

    [Fact]
    public void PreviewFiltersByNameAndGroupWithoutChangingImportedMods()
    {
        var profile = new ModProfile();
        profile.Mods.Add(new ModEntry { Name = "Textures", GroupName = "Graphics", Order = 1 });
        profile.Mods.Add(new ModEntry { Name = "Fixes", Order = 2 });
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));

        model.SearchText = " GRAPHICS ";
        Assert.Equal("Textures", Assert.Single(model.VisibleEntries).Name);
        model.SearchText = "fix";
        Assert.Equal("Fixes", Assert.Single(model.VisibleEntries).Name);
        Assert.Equal(2, profile.Mods.Count);
        model.ProfileName = " ";
        Assert.False(model.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingModCanBeLocatedBeforeImportWithoutLosingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));
        try
        {
            var found = Directory.CreateDirectory(Path.Combine(root, "found")).FullName;
            var repaired = Directory.CreateDirectory(Path.Combine(root, "repaired")).FullName;
            Directory.CreateDirectory(Path.Combine(repaired, "bin"));
            File.WriteAllText(Path.Combine(repaired, "bin", "xr_3da.exe"), string.Empty);
            var profile = new ModProfile { Name = "Import", GameInstallPath = root, UseBaseGameData = false };
            profile.Mods.Add(new ModEntry { Name = "Found", SourcePath = found, Order = 1 });
            profile.Mods.Add(new ModEntry { Name = "Missing", SourcePath = Path.Combine(root, "missing"), Order = 2, ExcludedFiles = ["file.ltx"] });
            profile.Mods.Add(new ModEntry { Name = "Disabled missing", SourcePath = Path.Combine(root, "disabled"), Order = 3, IsEnabled = false });
            var completed = false;
            using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));
            model.Completed += (_, _) => completed = true;

            Assert.False(model.ImportCommand.CanExecute(null));
            Assert.False(model.Entries[1].IsAvailable);
            Assert.True(model.BrowseMissingModFolderCommand.CanExecute(model.Entries[1]));
            model.ShowOnlyProblems = true;
            Assert.Equal(2, model.VisibleEntries.Count());
            model.Entries[1].SourcePath = repaired;

            Assert.True(model.Entries[1].IsAvailable);
            Assert.Single(model.VisibleEntries);
            Assert.True(model.ImportCommand.CanExecute(null));
            await model.PreviewRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("xr_3da.exe", model.ExecutableSummary, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("missing", profile.Mods[1].SourcePath);
            model.ImportCommand.Execute(null);

            Assert.True(completed);
            Assert.Equal(repaired, profile.Mods[1].SourcePath);
            Assert.Equal(["file.ltx"], profile.Mods[1].ExcludedFiles);
            Assert.Equal([1, 2, 3], profile.Mods.Select(mod => mod.Order));
            Assert.False(profile.Mods[2].IsEnabled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedCommitKeepsPreviewOpenAndAllowsRetry()
    {
        var attempt = 0;
        var completed = false;
        using var model = new ProfileImportViewModel(new ModProfile { Name = "Imported" }, _ =>
            Task.FromResult(++attempt > 1));
        model.Completed += (_, _) => completed = true;

        model.ImportCommand.Execute(null);
        Assert.False(completed);
        Assert.True(model.ImportCommand.CanExecute(null));
        model.ImportCommand.Execute(null);
        Assert.True(completed);
    }
}
