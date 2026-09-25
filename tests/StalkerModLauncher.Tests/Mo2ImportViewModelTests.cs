using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class Mo2ImportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherMo2ViewModelTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IncludeOverwriteRemainsDisabledAndUsvfsIsDefault()
    {
        var mo2Root = Directory.CreateDirectory(Path.Combine(_root, "MO2")).FullName;
        var gamePath = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        var modsPath = Directory.CreateDirectory(Path.Combine(mo2Root, "mods", "One")).Parent!.FullName;
        var profilePath = Directory.CreateDirectory(Path.Combine(mo2Root, "profiles", "Default")).FullName;
        var overwritePath = Directory.CreateDirectory(Path.Combine(mo2Root, "overwrite")).FullName;
        File.WriteAllText(Path.Combine(gamePath, "AnomalyLauncher.exe"), "test");
        File.WriteAllText(Path.Combine(profilePath, "modlist.txt"), "+One");
        File.WriteAllText(Path.Combine(overwritePath, "generated.ltx"), "test");
        File.WriteAllLines(Path.Combine(mo2Root, "ModOrganizer.ini"),
        [
            "[General]",
            $"gamePath={gamePath}",
            "selected_profile=@ByteArray(Default)",
            "[Settings]",
            $"base_directory={mo2Root}",
            $"mods_directory={modsPath}",
            $"profiles_directory={Path.Combine(mo2Root, "profiles")}",
            $"overwrite_directory={overwritePath}"
        ]);
        var importedProfile = new TaskCompletionSource<ModProfile>();
        var viewModel = new Mo2ImportViewModel(profile =>
        {
            importedProfile.SetResult(profile);
            return Task.FromResult(true);
        });

        viewModel.LoadSource(mo2Root);
        Assert.True(viewModel.NextCommand.CanExecute(null));
        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.Preview?.HasOverwriteContent);
        Assert.False(viewModel.IncludeOverwrite);
        viewModel.ImportCommand.Execute(null);
        Assert.Equal(LaunchBackendKind.VirtualFileSystem, (await importedProfile.Task).LaunchBackendKind);
    }

    [Fact]
    public void RemembersManualSourceAndRestoresItWithoutRememberingAgain()
    {
        var mo2Root = Directory.CreateDirectory(Path.Combine(_root, "RememberedMO2")).FullName;
        var gamePath = Directory.CreateDirectory(Path.Combine(_root, "RememberedGame")).FullName;
        Directory.CreateDirectory(Path.Combine(mo2Root, "mods", "One"));
        var profilePath = Directory.CreateDirectory(Path.Combine(mo2Root, "profiles", "Default")).FullName;
        File.WriteAllText(Path.Combine(profilePath, "modlist.txt"), "+One");
        File.WriteAllLines(Path.Combine(mo2Root, "ModOrganizer.ini"),
        [
            $"gamePath={gamePath}",
            $"base_directory={mo2Root}",
            "mods_directory=mods",
            "profiles_directory=profiles"
        ]);
        string? remembered = null;
        var viewModel = new Mo2ImportViewModel(
            _ => Task.FromResult(true),
            rememberSource: path => remembered = path);

        viewModel.LoadSource(mo2Root);

        Assert.Equal(mo2Root, remembered);
        remembered = null;
        var restored = new Mo2ImportViewModel(
            _ => Task.FromResult(true),
            mo2Root,
            path => remembered = path);
        Assert.Equal(mo2Root, restored.SourcePath);
        Assert.Null(remembered);
        Assert.True(restored.UseVirtualFileSystem);
        Assert.False(restored.UseLinkedWorkspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SearchTextFiltersVisibleEntriesByName()
    {
        var mo2Root = CreateMo2("Search", ["Alpha", "Beta"], "+Alpha\n+Beta");
        var viewModel = new Mo2ImportViewModel(_ => Task.FromResult(true));
        viewModel.LoadSource(mo2Root);
        viewModel.NextCommand.Execute(null);

        Assert.Equal(2, viewModel.VisibleEntries.Count());
        viewModel.SearchText = "alp";
        var filtered = viewModel.VisibleEntries.ToArray();
        Assert.Single(filtered);
        Assert.Equal("Alpha", filtered[0].Name);
        viewModel.SearchText = "zzz";
        Assert.Empty(viewModel.VisibleEntries);
        viewModel.SearchText = string.Empty;
        Assert.Equal(2, viewModel.VisibleEntries.Count());
        viewModel.ShowOnlyProblems = true;
        Assert.Empty(viewModel.VisibleEntries);
    }

    [Fact]
    public void BlockedReasonsAndStepTitleFollowWizardState()
    {
        var viewModel = new Mo2ImportViewModel(_ => Task.FromResult(true));
        Assert.Equal(Strings.Mo2_StepSourceTitle, viewModel.StepTitle);
        Assert.False(string.IsNullOrEmpty(viewModel.NextBlockedReason));
        Assert.Empty(viewModel.ImportBlockedReason);

        var mo2Root = CreateMo2("Blocked", ["Alpha"], "+Alpha");
        viewModel.LoadSource(mo2Root);
        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsPreviewStep);
        Assert.Equal(Strings.Mo2_StepPreviewTitle, viewModel.StepTitle);
        Assert.Empty(viewModel.NextBlockedReason);
        viewModel.ProfileName = string.Empty;
        Assert.Equal(Strings.Mo2_ImportBlockedName, viewModel.ImportBlockedReason);
        Assert.False(viewModel.ImportCommand.CanExecute(null));
        viewModel.ProfileName = "Imported";
        Assert.Empty(viewModel.ImportBlockedReason);
        Assert.True(viewModel.ImportCommand.CanExecute(null));
    }

    private string CreateMo2(string name, string[] mods, string modList)
    {
        var mo2Root = Directory.CreateDirectory(Path.Combine(_root, name, "MO2")).FullName;
        var gamePath = Directory.CreateDirectory(Path.Combine(_root, name, "Game")).FullName;
        var modsPath = Directory.CreateDirectory(Path.Combine(mo2Root, "mods")).FullName;
        foreach (var mod in mods)
        {
            Directory.CreateDirectory(Path.Combine(modsPath, mod));
        }

        var profilePath = Directory.CreateDirectory(Path.Combine(mo2Root, "profiles", "Default")).FullName;
        File.WriteAllText(Path.Combine(profilePath, "modlist.txt"), modList);
        File.WriteAllLines(Path.Combine(mo2Root, "ModOrganizer.ini"),
        [
            "[General]",
            $"gamePath={gamePath}",
            "selected_profile=@ByteArray(Default)",
            "[Settings]",
            $"base_directory={mo2Root}",
            $"mods_directory={modsPath}",
            $"profiles_directory={Path.Combine(mo2Root, "profiles")}",
            $"overwrite_directory={Path.Combine(mo2Root, "overwrite")}"
        ]);
        return mo2Root;
    }
}
