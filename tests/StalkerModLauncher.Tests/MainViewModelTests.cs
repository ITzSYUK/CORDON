using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class MainViewModelTests
{
    private static readonly string[] BoundaryTestModNames = ["First", "Second", "Third", "Fourth"];
    private static readonly string[] GroupBoundaryTestModNames = [.. BoundaryTestModNames, "Fifth"];

    [Fact]
    public async Task LaunchProfileDoesNotChangeSelectedProfile()
    {
        await RunWithViewModelAsync(async (viewModel, _) =>
        {
            var selected = new ModProfile { Name = "Selected" };
            var trayProfile = new ModProfile { Name = "Tray profile" };
            viewModel.AddCreatedProfile(selected);
            viewModel.AddCreatedProfile(trayProfile);
            viewModel.SelectedProfile = selected;

            await viewModel.LaunchProfileAsync(trayProfile);

            Assert.Same(selected, viewModel.SelectedProfile);
        });
    }

    [Fact]
    public async Task LaunchProfileRefreshesCachedReadinessBeforeStarting()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var profile = new ModProfile
            {
                Name = "Readiness refresh",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            viewModel.AddCreatedProfile(profile);
            Assert.True(viewModel.CanLaunchProfile(profile));

            File.Delete(Path.Combine(gameRoot, "bin", "xr_3da.exe"));
            await viewModel.LaunchProfileAsync(profile);

            Assert.False(profile.IsRunning);
        });
    }

    [Fact]
    public async Task CanLaunchProfileExposesValidationErrorForTrayIndicator()
    {
        await RunWithViewModelAsync(async (viewModel, _) =>
        {
            var profile = new ModProfile { Name = "Broken profile" };
            viewModel.AddCreatedProfile(profile);

            Assert.False(viewModel.CanLaunchProfile(profile));
            Assert.True(profile.HasLaunchError);
            Assert.False(string.IsNullOrWhiteSpace(profile.LaunchErrorSummary));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RefreshProfileLaunchReadinessRefreshesFixedProfileWithoutRestart()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var executablePath = Path.Combine(gameRoot, "bin", "xr_3da.exe");
            var profile = new ModProfile
            {
                Name = "Fixed profile",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            viewModel.AddCreatedProfile(profile);

            File.Delete(executablePath);
            viewModel.RefreshProfileLaunchReadiness(forceRefresh: true);
            Assert.True(profile.HasLaunchError);
            Assert.False(viewModel.CanLaunchProfile(profile));

            File.WriteAllText(executablePath, "game");
            viewModel.RefreshProfileLaunchReadiness(forceRefresh: true);

            Assert.False(profile.HasLaunchError);
            Assert.True(viewModel.CanLaunchProfile(profile));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SavingLauncherSettingsUpdatesWindowsStartupRegistration()
    {
        var startup = new CapturingStartupRegistrationService();
        await RunWithViewModelAsync(async (viewModel, _) =>
        {
            var settings = viewModel.CreateLauncherSettingsViewModel();
            settings.StartWithWindows = true;

            Assert.True(await settings.TrySaveAsync());
            Assert.Equal([(true, true)], startup.Values);

            startup.Values.Clear();
            settings = viewModel.CreateLauncherSettingsViewModel();
            settings.StartMinimizedToTrayOnWindowsStartup = false;

            Assert.True(await settings.TrySaveAsync());
            Assert.Equal([(true, false)], startup.Values);
        }, startupRegistrationService: startup);
    }

    [Fact]
    public async Task LoadingEnabledWindowsStartupRefreshesRegistration()
    {
        var startup = new CapturingStartupRegistrationService();
        await RunWithViewModelAsync(
            (viewModel, _) =>
            {
                Assert.True(viewModel.StartWithWindows);
                Assert.Equal([(true, false)], startup.Values);
                return Task.CompletedTask;
            },
            startupRegistrationService: startup,
            initialSettings: new AppSettings
            {
                StartWithWindows = true,
                StartMinimizedToTrayOnWindowsStartup = false
            });
    }

    [Fact]
    public async Task SavingLauncherSettingsCanHideTrayIconSafely()
    {
        await RunWithViewModelAsync(async (viewModel, _) =>
        {
            var settings = viewModel.CreateLauncherSettingsViewModel();
            settings.ShowTrayIcon = false;

            Assert.True(await settings.TrySaveAsync());
            Assert.False(viewModel.ShowTrayIcon);
            Assert.False(viewModel.StartMinimizedToTrayOnWindowsStartup);
            Assert.False(viewModel.MinimizeToTrayOnClose);
        });
    }

    [Fact]
    public async Task ResettingLauncherSettingsPreservesProfilesAndMods()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Preserved profile" };
            profile.Mods.Add(new ModEntry { Name = "Preserved mod", Order = 1 });
            viewModel.AddCreatedProfile(profile);
            var settings = viewModel.CreateLauncherSettingsViewModel(confirmReset: () => true);

            settings.ResetCommand.Execute(null);
            Assert.True(await settings.TrySaveAsync());

            using var store = new SettingsStore(new AppPaths(
                Path.Combine(root, "config"),
                Path.Combine(root, "workspaces"),
                preferGameDriveWorkspace: false));
            var persisted = await store.LoadAsync();
            var persistedProfile = Assert.Single(persisted.Profiles);
            Assert.Equal("Preserved profile", persistedProfile.Name);
            Assert.Equal("Preserved mod", Assert.Single(persistedProfile.Mods).Name);
        });
    }

    [Fact]
    public async Task NewProfileCommandRaisesProfileCreationRequest()
    {
        await RunWithViewModelAsync((viewModel, _) =>
        {
            var raised = false;
            viewModel.ProfileCreationRequested += (_, _) => raised = true;

            viewModel.NewProfileCommand.Execute(null);

            Assert.True(raised);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task HasProfilesTracksProfileCollectionState()
    {
        await RunWithViewModelAsync((viewModel, _) =>
        {
            Assert.False(viewModel.HasProfiles);

            var profile = new ModProfile { Name = "First profile" };
            viewModel.AddCreatedProfile(profile);

            Assert.True(viewModel.HasProfiles);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AddCreatedProfileSelectsProfileAndDoesNotInheritPreviousGamePath()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = Path.Combine(root, "game");
            var existing = new ModProfile { Name = "Profile", GameInstallPath = gameRoot };
            viewModel.AddCreatedProfile(existing);

            var created = new ModProfile { Name = "Profile" };
            viewModel.AddCreatedProfile(created);

            Assert.Equal("Profile (2)", created.Name);
            Assert.Same(created, viewModel.SelectedProfile);
            Assert.Empty(created.GameInstallPath);
            Assert.Empty(viewModel.GameInstallPath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DuplicateProfileCommandCreatesIndependentCopyAndSelectsIt()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var source = new ModProfile
            {
                Name = "Anomaly",
                Description = "Test profile",
                GameInstallPath = Path.Combine(root, "game"),
                LaunchArguments = "-nointro -test",
                TotalPlaytimeSeconds = 42,
                LastPlayedAt = DateTime.Now
            };
            source.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(root, "mods", "patch"), Order = 1 });
            viewModel.AddCreatedProfile(source);

            Assert.True(viewModel.DuplicateProfileCommand.CanExecute(null));
            viewModel.DuplicateProfileCommand.Execute(null);

            var duplicate = Assert.Single(viewModel.Profiles, profile => profile != source);
            Assert.Same(duplicate, viewModel.SelectedProfile);
            Assert.Equal("Anomaly — копия", duplicate.Name);
            Assert.NotEqual(source.Id, duplicate.Id);
            Assert.Equal(source.GameInstallPath, duplicate.GameInstallPath);
            Assert.NotEqual(source.WorkspacePath, duplicate.WorkspacePath);
            Assert.EndsWith($"profile-{duplicate.Id}", duplicate.WorkspacePath);
            Assert.Equal(0, duplicate.TotalPlaytimeSeconds);
            Assert.Null(duplicate.LastPlayedAt);
            Assert.Single(duplicate.Mods);
            Assert.NotSame(source.Mods[0], duplicate.Mods[0]);
            Assert.NotEqual(source.Mods[0].Id, duplicate.Mods[0].Id);
            Assert.Equal(source.Mods[0].SourcePath, duplicate.Mods[0].SourcePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectedProfileControlsProfileActionCommandAvailability()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            Assert.False(viewModel.DuplicateProfileCommand.CanExecute(null));
            Assert.False(viewModel.DeleteProfileCommand.CanExecute(null));
            Assert.False(viewModel.ExportProfileCommand.CanExecute(null));
            Assert.False(viewModel.OpenProfileFolderCommand.CanExecute(null));

            var profile = new ModProfile { Name = "Actions", GameInstallPath = Path.Combine(root, "game") };
            viewModel.AddCreatedProfile(profile);

            Assert.True(viewModel.DuplicateProfileCommand.CanExecute(null));
            Assert.True(viewModel.DeleteProfileCommand.CanExecute(null));
            Assert.True(viewModel.ExportProfileCommand.CanExecute(null));
            Assert.True(viewModel.OpenProfileFolderCommand.CanExecute(null));
            Assert.True(viewModel.InlineExportProfileCommand.CanExecute(profile));

            profile.IsRunning = true;

            Assert.False(viewModel.InlineDeleteProfileCommand.CanExecute(profile));
            Assert.True(viewModel.InlineExportProfileCommand.CanExecute(profile));

            viewModel.SelectedProfile = null;

            Assert.False(viewModel.DuplicateProfileCommand.CanExecute(null));
            Assert.False(viewModel.DeleteProfileCommand.CanExecute(null));
            Assert.False(viewModel.ExportProfileCommand.CanExecute(null));
            Assert.False(viewModel.OpenProfileFolderCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MoveModToInsertionIndexReordersSelectedProfileAndRenumbersMods()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Overlay", GameInstallPath = Path.Combine(root, "game") };
            profile.Mods.Add(new ModEntry { Name = "Base mod", SourcePath = Path.Combine(root, "mods", "base"), GroupName = "1", Order = 1 });
            profile.Mods.Add(new ModEntry { Name = "Patch", SourcePath = Path.Combine(root, "mods", "patch"), GroupName = "1", Order = 2 });
            profile.Mods.Add(new ModEntry { Name = "Fix", SourcePath = Path.Combine(root, "mods", "fix"), GroupName = "2", Order = 3 });
            viewModel.AddCreatedProfile(profile);

            var moved = profile.Mods[0];
            viewModel.MoveModToInsertionIndex(moved, 3);

            Assert.Equal(["Patch", "Fix", "Base mod"], profile.Mods.Select(mod => mod.Name));
            Assert.Equal(["1", "2", ""], profile.Mods.Select(mod => mod.GroupName));
            Assert.Equal([1, 2, 3], profile.Mods.Select(mod => mod.Order));
            Assert.Same(moved, viewModel.SelectedMod);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TryAddImportedProfileAsyncRollsBackWhenSettingsCannotBeSaved()
    {
        var dialogService = new CapturingDialogService();
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var settingsPath = Path.Combine(root, "config", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllTextAsync(settingsPath, "{}");
            await using var lockedSettings = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var imported = new ModProfile { Name = "MO2 import" };

            var result = await viewModel.TryAddImportedProfileAsync(imported);

            Assert.False(result);
            Assert.Empty(viewModel.Profiles);
            Assert.Null(viewModel.SelectedProfile);
            Assert.Contains("изменения отменены", dialogService.LastError, StringComparison.OrdinalIgnoreCase);
        }, dialogService);
    }

    [Fact]
    public async Task MoveModsToBoundariesReordersWholeSelection()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Overlay", GameInstallPath = Path.Combine(root, "game") };
            foreach (var name in BoundaryTestModNames)
            {
                profile.Mods.Add(new ModEntry { Name = name, SourcePath = Path.Combine(root, "mods", name) });
            }

            viewModel.AddCreatedProfile(profile);
            var selected = new[] { profile.Mods[1], profile.Mods[3] };

            viewModel.MoveModsToStart(selected);
            Assert.Equal(["Second", "Fourth", "First", "Third"], profile.Mods.Select(mod => mod.Name));

            viewModel.MoveModsToEnd(selected);
            Assert.Equal(["First", "Third", "Second", "Fourth"], profile.Mods.Select(mod => mod.Name));
            Assert.Equal([1, 2, 3, 4], profile.Mods.Select(mod => mod.Order));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeletedAndRestoredModFolderRefreshesProfileWithoutSwitchingProfiles()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var modRoot = Directory.CreateDirectory(Path.Combine(root, "mods", "watched")).FullName;
            var profile = new ModProfile
            {
                Name = "Watched mod",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe",
                Mods = { new ModEntry { Name = "Watched", SourcePath = modRoot, IsEnabled = true } }
            };
            viewModel.AddCreatedProfile(profile);
            Assert.True(viewModel.IsGameValid);

            Directory.Delete(modRoot);

            await WaitUntilAsync(() => profile.HasLaunchError && !viewModel.IsGameValid);
            Assert.Contains("Watched", profile.LaunchErrorSummary);

            Directory.CreateDirectory(modRoot);

            await WaitUntilAsync(() => !profile.HasLaunchError && viewModel.IsGameValid);
        });
    }

    [Fact]
    public async Task DeletedAutomaticExecutableRefreshesProviderWithoutChangingSelection()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var modRoot = Directory.CreateDirectory(Path.Combine(root, "mods", "engine")).FullName;
            var modBin = Directory.CreateDirectory(Path.Combine(modRoot, "bin_x64")).FullName;
            var modExecutable = Path.Combine(modBin, "xrEngine.exe");
            File.WriteAllText(modExecutable, string.Empty);
            var engineMod = new ModEntry { Name = "Engine", SourcePath = modRoot, IsEnabled = true };
            var profile = new ModProfile
            {
                Name = "Automatic executable",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin_x64\xrEngine.exe",
                Mods = { engineMod }
            };
            viewModel.AddCreatedProfile(profile);
            await WaitUntilAsync(() => engineMod.ProvidesLaunchExecutable);

            File.Delete(modExecutable);

            await WaitUntilAsync(() => !engineMod.ProvidesLaunchExecutable);
            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);

            File.WriteAllText(modExecutable, string.Empty);

            await WaitUntilAsync(() => engineMod.ProvidesLaunchExecutable);
            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);
        });
    }

    [Fact]
    public async Task ModifiedFsgameRefreshesProfileWithoutSwitchingProfiles()
    {
        await RunWithViewModelAsync(async (viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var fsgamePath = Path.Combine(gameRoot, "custom.ltx");
            File.WriteAllText(fsgamePath, "$app_data_root$ = true | false | $fs_root$ | appdata");
            var profile = new ModProfile
            {
                Name = "Watched fsgame",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            viewModel.AddCreatedProfile(profile);
            profile.LaunchArguments = "-fsltx custom.ltx";
            Assert.True(viewModel.IsGameValid);

            File.WriteAllText(fsgamePath, "invalid");

            await WaitUntilAsync(() => profile.HasLaunchError && !viewModel.IsGameValid);

            File.WriteAllText(fsgamePath, "$app_data_root$ = true | false | $fs_root$ | appdata");

            await WaitUntilAsync(() => !profile.HasLaunchError && viewModel.IsGameValid);
        });
    }

    [Fact]
    public void MissingModDirectlyUnderDriveRootWatchesDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var target = Path.Combine(driveRoot, $"watched-mod-{Guid.NewGuid():N}");

        var directories = MainViewModel.GetWatchDirectories(target).ToArray();

        Assert.Equal([driveRoot], directories);
    }

    [Fact]
    public async Task MoveModsWithinGroupToBoundariesKeepsSelectionInItsGroup()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Overlay", GameInstallPath = Path.Combine(root, "game") };
            foreach (var name in GroupBoundaryTestModNames)
            {
                profile.Mods.Add(new ModEntry
                {
                    Name = name,
                    SourcePath = Path.Combine(root, "mods", name),
                    GroupName = name == "First" ? string.Empty : "Gameplay"
                });
            }

            viewModel.AddCreatedProfile(profile);
            var selected = new[] { profile.Mods[2], profile.Mods[4] };
            var viewResets = 0;
            ((INotifyCollectionChanged)viewModel.FilteredMods!).CollectionChanged += (_, e) =>
                viewResets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

            viewModel.MoveModsWithinGroupToBoundary(selected, moveToEnd: false);
            Assert.Equal(["First", "Third", "Fifth", "Second", "Fourth"], profile.Mods.Select(mod => mod.Name));

            viewModel.MoveModsWithinGroupToBoundary(selected, moveToEnd: true);
            Assert.Equal(["First", "Second", "Fourth", "Third", "Fifth"], profile.Mods.Select(mod => mod.Name));
            Assert.All(profile.Mods.Skip(1), mod => Assert.Equal("Gameplay", mod.GroupName));
            Assert.Equal(0, viewResets);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LargeGroupedModReorderDoesNotResetTheView()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Large overlay", GameInstallPath = Path.Combine(root, "game") };
            for (var index = 0; index < 455; index++)
            {
                profile.Mods.Add(new ModEntry
                {
                    Name = $"Mod {index}",
                    SourcePath = Path.Combine(root, "mods", index.ToString(CultureInfo.InvariantCulture)),
                    GroupName = $"Group {index / 10}"
                });
            }

            viewModel.AddCreatedProfile(profile);
            Assert.Empty(viewModel.FilteredMods!.GroupDescriptions);
            Assert.Equal(46, profile.Mods.Count(mod => mod.ShowsGroupHeader));
            var viewResets = 0;
            ((INotifyCollectionChanged)viewModel.FilteredMods).CollectionChanged += (_, e) =>
                viewResets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

            var source = profile.Mods[307];
            var target = profile.Mods[317];
            viewModel.MoveModsToInsertionIndex([source], profile.Mods.IndexOf(target) + 1, target.GroupName);

            Assert.Equal(0, viewResets);
            viewModel.SetModGroupCollapsed(target.GroupName, collapsed: true);
            Assert.Equal(445, viewModel.FilteredMods.Cast<ModEntry>().Count());
            Assert.True(viewModel.FilteredMods.Cast<ModEntry>().Single(mod => mod.ShowsGroupHeader && mod.GroupName == target.GroupName).IsGroupCollapsed);
            viewModel.SetModGroupCollapsed(target.GroupName, collapsed: false);
            Assert.Equal(455, viewModel.FilteredMods.Cast<ModEntry>().Count());
            Assert.Equal(2, viewResets);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MoveModGroupByOffsetMovesWholeGroupOneGroupAtATime()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Overlay", GameInstallPath = Path.Combine(root, "game") };
            profile.Mods.Add(new ModEntry { Name = "A1", GroupName = "A" });
            profile.Mods.Add(new ModEntry { Name = "A2", GroupName = "A" });
            profile.Mods.Add(new ModEntry { Name = "B1", GroupName = "B" });
            profile.Mods.Add(new ModEntry { Name = "C1", GroupName = "C" });
            profile.Mods.Add(new ModEntry { Name = "C2", GroupName = "C" });
            viewModel.AddCreatedProfile(profile);

            viewModel.MoveModGroupByOffset("B", 1);
            Assert.Equal(["A1", "A2", "C1", "C2", "B1"], profile.Mods.Select(mod => mod.Name));

            viewModel.MoveModGroupByOffset("B", -1);
            Assert.Equal(["A1", "A2", "B1", "C1", "C2"], profile.Mods.Select(mod => mod.Name));

            viewModel.MoveModGroupByOffset("B", -1);
            Assert.Equal(["B1", "A1", "A2", "C1", "C2"], profile.Mods.Select(mod => mod.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SetModGroupEnabledChangesOnlyTheRequestedGroup()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var profile = new ModProfile { Name = "Overlay", GameInstallPath = Path.Combine(root, "game") };
            profile.Mods.Add(new ModEntry { Name = "A1", GroupName = "A", IsEnabled = true });
            profile.Mods.Add(new ModEntry { Name = "A2", GroupName = "A", IsEnabled = true });
            profile.Mods.Add(new ModEntry { Name = "B1", GroupName = "B", IsEnabled = true });
            viewModel.AddCreatedProfile(profile);

            viewModel.SetModGroupEnabled("A", enabled: false);

            Assert.False(profile.Mods[0].IsEnabled);
            Assert.False(profile.Mods[1].IsEnabled);
            Assert.True(profile.Mods[2].IsEnabled);
            viewModel.SetModGroupEnabled("A", enabled: true);
            Assert.All(profile.Mods.Take(2), mod => Assert.True(mod.IsEnabled));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AddDroppedModsIgnoresDuplicatesAndStandaloneProfileLimit()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var firstMod = Directory.CreateDirectory(Path.Combine(root, "mods", "first")).FullName;
            var secondMod = Directory.CreateDirectory(Path.Combine(root, "mods", "second")).FullName;
            var profile = new ModProfile { Name = "Standalone", IsStandalone = true };
            viewModel.AddCreatedProfile(profile);

            viewModel.AddDroppedMods([firstMod, firstMod, secondMod]);

            Assert.Single(profile.Mods);
            Assert.Equal(firstMod, profile.Mods[0].SourcePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ModToggleRefreshesAutomaticallySelectedExecutable()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var engineModRoot = Directory.CreateDirectory(Path.Combine(root, "mods", "engine")).FullName;
            Directory.CreateDirectory(Path.Combine(engineModRoot, "bin_x64"));
            File.WriteAllText(Path.Combine(engineModRoot, "bin_x64", "xrEngine.exe"), string.Empty);
            var engineMod = new ModEntry
            {
                Name = "Engine",
                SourcePath = engineModRoot,
                IsEnabled = true,
                Order = 1
            };
            var profile = new ModProfile
            {
                Name = "Automatic executable",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin_x64\xrEngine.exe",
                ExecutableSourcePath = string.Empty,
                Mods = { engineMod }
            };
            viewModel.AddCreatedProfile(profile);

            engineMod.IsEnabled = false;

            Assert.Equal(@"bin\xr_3da.exe", profile.ExecutableRelativePath);

            engineMod.IsEnabled = true;

            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ModToggleDoesNotReplaceManuallyPinnedExecutable()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var engineModRoot = Directory.CreateDirectory(Path.Combine(root, "mods", "engine")).FullName;
            Directory.CreateDirectory(Path.Combine(engineModRoot, "bin_x64"));
            File.WriteAllText(Path.Combine(engineModRoot, "bin_x64", "xrEngine.exe"), string.Empty);
            var engineMod = new ModEntry
            {
                Name = "Engine",
                SourcePath = engineModRoot,
                IsEnabled = true,
                Order = 1
            };
            var profile = new ModProfile
            {
                Name = "Pinned executable",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin_x64\xrEngine.exe",
                ExecutableSourcePath = engineModRoot,
                Mods = { engineMod }
            };
            viewModel.AddCreatedProfile(profile);

            engineMod.IsEnabled = false;

            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);
            Assert.Equal(engineModRoot, profile.ExecutableSourcePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ModOrderAutomaticallyChoosesHighestPriorityBinVariantEngineWithoutPinningSource()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var firstMod = Directory.CreateDirectory(Path.Combine(root, "mods", "first")).FullName;
            var secondMod = Directory.CreateDirectory(Path.Combine(root, "mods", "second")).FullName;
            Directory.CreateDirectory(Path.Combine(firstMod, "bin_OGSR"));
            Directory.CreateDirectory(Path.Combine(secondMod, "bin_x64"));
            File.WriteAllText(Path.Combine(firstMod, "bin_OGSR", "xrEngine.exe"), string.Empty);
            File.WriteAllText(Path.Combine(secondMod, "bin_x64", "xrEngine.exe"), string.Empty);
            var profile = new ModProfile
            {
                Name = "Automatic OGSR engine",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            viewModel.AddCreatedProfile(profile);

            viewModel.AddDroppedMods([firstMod, secondMod]);

            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);
            Assert.Empty(profile.ExecutableSourcePath);

            viewModel.MoveModToEnd(profile.Mods[0]);

            Assert.Equal(@"bin_OGSR\xrEngine.exe", profile.ExecutableRelativePath);
            Assert.Empty(profile.ExecutableSourcePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunningProfileBlocksModCommandsAndDirectModMutations()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var firstMod = Directory.CreateDirectory(Path.Combine(root, "mods", "first")).FullName;
            var secondMod = Directory.CreateDirectory(Path.Combine(root, "mods", "second")).FullName;
            var extraMod = Directory.CreateDirectory(Path.Combine(root, "mods", "extra")).FullName;
            var profile = new ModProfile
            {
                Name = "Running",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            profile.Mods.Add(new ModEntry { Name = "First", SourcePath = firstMod, Order = 1 });
            profile.Mods.Add(new ModEntry { Name = "Second", SourcePath = secondMod, Order = 2 });
            viewModel.AddCreatedProfile(profile);
            viewModel.SelectedMod = profile.Mods[0];

            Assert.True(viewModel.IsGameValid);

            profile.IsRunning = true;

            Assert.False(viewModel.CanEditSelectedProfile);
            Assert.False(viewModel.AddModCommand.CanExecute(null));
            Assert.False(viewModel.RemoveModCommand.CanExecute(null));
            Assert.False(viewModel.MoveModDownCommand.CanExecute(null));
            Assert.False(viewModel.InlineRemoveModCommand.CanExecute(profile.Mods[0]));
            Assert.False(viewModel.InlineMoveModDownCommand.CanExecute(profile.Mods[0]));
            Assert.False(viewModel.ScanForModsCommand.CanExecute(null));
            Assert.False(viewModel.LaunchCommand.CanExecute(null));

            viewModel.AddDroppedMods([extraMod]);
            viewModel.MoveModToInsertionIndex(profile.Mods[0], 2);
            viewModel.RemoveMods([profile.Mods[0]]);

            Assert.Equal(["First", "Second"], profile.Mods.Select(mod => mod.Name));
            return Task.CompletedTask;
        });
    }

    private static async Task RunWithViewModelAsync(
        Func<MainViewModel, string, Task> test,
        DialogService? dialogService = null,
        IStartupRegistrationService? startupRegistrationService = null,
        AppSettings? initialSettings = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "StalkerModLauncherTests", Guid.NewGuid().ToString("N"));
        MainViewModel? viewModel = null;
        try
        {
            if (initialSettings is not null)
            {
                using var initialStore = new SettingsStore(new AppPaths(
                    Path.Combine(root, "config"),
                    Path.Combine(root, "workspaces"),
                    preferGameDriveWorkspace: false));
                await initialStore.SaveAsync(initialSettings);
            }

            viewModel = CreateViewModel(root, dialogService, startupRegistrationService);
            await WaitForSettingsLoadedAsync(viewModel);
            await test(viewModel, root);
        }
        finally
        {
            if (viewModel is not null)
            {
                await viewModel.CleanupAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MainViewModel CreateViewModel(
        string root,
        DialogService? dialogService = null,
        IStartupRegistrationService? startupRegistrationService = null)
    {
        var paths = new AppPaths(
            Path.Combine(root, "config"),
            Path.Combine(root, "workspaces"),
            preferGameDriveWorkspace: false);
        var settingsStore = new SettingsStore(paths);
        var profileManager = new ProfileManager(paths, new FakeProfileWorkspaceManager());
        return new MainViewModel(
            paths,
            settingsStore,
            new LaunchCoordinator(new ThrowingProfileLauncher(), new FakeGameSessionTracker()),
            dialogService ?? new DialogService(),
            new ModConflictAnalyzer(),
            profileManager,
            new LaunchPreflightService(profileManager),
            new ApplicationLogService(paths),
            startupRegistrationService);
    }

    [Fact]
    public async Task ReplacingModCollectionKeepsAutomaticExecutableSelectionSubscribed()
    {
        await RunWithViewModelAsync((viewModel, root) =>
        {
            var gameRoot = CreateValidGameRoot(root);
            var engineModRoot = Directory.CreateDirectory(Path.Combine(root, "mods", "replacement-engine")).FullName;
            Directory.CreateDirectory(Path.Combine(engineModRoot, "bin_x64"));
            File.WriteAllText(Path.Combine(engineModRoot, "bin_x64", "xrEngine.exe"), string.Empty);
            var profile = new ModProfile
            {
                Name = "Replaced mod collection",
                GameInstallPath = gameRoot,
                ExecutableRelativePath = @"bin\xr_3da.exe"
            };
            viewModel.AddCreatedProfile(profile);

            profile.Mods = new ObservableCollection<ModEntry>
            {
                new()
                {
                    Name = "Replacement engine",
                    SourcePath = engineModRoot,
                    Order = 1,
                    IsEnabled = true
                }
            };

            Assert.Equal(@"bin_x64\xrEngine.exe", profile.ExecutableRelativePath);
            Assert.Same(profile.Mods, viewModel.FilteredMods?.SourceCollection);
            return Task.CompletedTask;
        });
    }

    private sealed class CapturingStartupRegistrationService : IStartupRegistrationService
    {
        public List<(bool Enabled, bool Minimized)> Values { get; } = [];

        public void Configure(bool enabled, bool startMinimizedToTray) =>
            Values.Add((enabled, startMinimizedToTray));
    }

    private sealed class CapturingDialogService : DialogService
    {
        public string LastError { get; private set; } = string.Empty;

        public override void ShowError(string title, string message)
        {
            LastError = $"{title}: {message}";
        }
    }

    private static async Task WaitForSettingsLoadedAsync(MainViewModel viewModel)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (viewModel.ActivityLog.Entries.Any(entry => entry.Contains(Strings.Log_SettingsLoaded, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("MainViewModel did not finish loading test settings.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for an automatic profile refresh.");
    }

    private static string CreateValidGameRoot(string root)
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
        File.WriteAllText(Path.Combine(gameRoot, "bin", "xr_3da.exe"), string.Empty);
        File.WriteAllText(
            Path.Combine(gameRoot, "fsgame.ltx"),
            "$app_data_root$ = true | false | $fs_root$ | appdata");
        return gameRoot;
    }

    private sealed class FakeProfileWorkspaceManager : IProfileWorkspaceManager
    {
        public string EnsureProfileWorkspace(ModProfile profile, string gamePath, IProgress<string>? progress = null)
        {
            return string.IsNullOrWhiteSpace(profile.WorkspacePath)
                ? Path.Combine(Path.GetTempPath(), $"profile-{profile.Id}")
                : profile.WorkspacePath;
        }

        public void DeleteProfileWorkspace(ModProfile profile, string gamePath)
        {
        }

        public void ClearProfileWorkspaceCache(ModProfile profile, string gamePath)
        {
        }
    }

    private sealed class ThrowingProfileLauncher : IProfileLauncher
    {
        public Task<ProfileLaunchHandle> LaunchAsync(
            string gamePath,
            ModProfile profile,
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("MainViewModel tests must not launch game processes.");
        }
    }

    private sealed class FakeGameSessionTracker : IGameSessionTracker
    {
        public void ConfigureDiscord(string clientId, Action<string>? diagnostic = null)
        {
        }

        public Task<GameSessionResult> TrackAsync(
            Process process,
            string profileName,
            bool publishDiscordStatus,
            Task<int>? sessionCompletion = null)
        {
            throw new NotSupportedException("MainViewModel tests must not track game processes.");
        }

        public void Dispose()
        {
        }
    }
}
