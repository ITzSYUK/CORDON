using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.Services.Fomod;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views;
using StalkerModLauncher.Views.Controls;
using Xunit;

namespace StalkerModLauncher.Tests;

[Collection("Fomod UI")]
public sealed class FomodUiTests
{
    [Fact]
    public void ClassicAndBothPdaThemesPassWizardSmokeTest()
    {
        RunSta(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/CORDON;component/Themes/Palette.xaml", UriKind.Relative)
            });
            application.Resources = ReadAppResources();
            Exception? failure = null;
            application.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    StorageMigrationWindowRenders();
                    await StorageMigrationPageBlocksNavigationDuringTransferAsync();
                    ScreenshotsViewsUseViewportLoading();
                    await ModListScrollResetsWhenProfileChangesAsync();
                    ProfileCreationRendersAndNavigates("classic");
                    ProfileCreationRendersAndNavigates("pda");
                    ProfileCreationRendersAndNavigates("pda-new");
                    WizardRendersNavigatesAndInstallsThroughBoundControls("classic");
                    WizardRendersNavigatesAndInstallsThroughBoundControls("pda");
                    WizardRendersNavigatesAndInstallsThroughBoundControls("pda-new");
                    await LauncherSettingsImportButtonRendersAndRunsAsync("classic");
                    await LauncherSettingsImportButtonRendersAndRunsAsync("pda");
                    await LauncherSettingsImportButtonRendersAndRunsAsync("pda-new");
                    ClassicWindowCloseCancelsAndPdaPageReplacementCancels();
                    await FolderRequestsUseExistingWizardForBothInterfacesAsync(application);
                }
                catch (Exception ex) { failure = ex; }
                finally { application.Shutdown(); }
            }));
            application.Run();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        });
    }

    private static async Task ModListScrollResetsWhenProfileChangesAsync()
    {
        var panel = new ModPanelView { Resources = ReadAppResources() };
        var mods = Enumerable.Range(1, 100).Select(i => new ModEntry { Name = $"Mod {i}" }).ToArray();
        panel.DataContext = new { SelectedProfile = new ModProfile(), FilteredMods = mods };
        Layout(panel, 800, 500);
        await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        var list = Assert.IsType<ListView>(panel.FindName("ModsList"));
        var scroll = Descendants<ScrollViewer>(list).First();
        scroll.ScrollToEnd();
        panel.UpdateLayout();
        Assert.True(scroll.VerticalOffset > 0);

        panel.DataContext = new { SelectedProfile = new ModProfile(), FilteredMods = mods };
        await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        panel.UpdateLayout();
        Assert.Equal(0, scroll.VerticalOffset);
    }

    private static void ScreenshotsViewsUseViewportLoading()
    {
        using var classicViewModel = new ScreenshotsViewModel(new ModProfile(), new ScreenshotClipboardService());
        using var pdaViewModel = new ScreenshotsViewModel(new ModProfile(), new ScreenshotClipboardService());
        var classic = new ScreenshotsWindow(classicViewModel) { Resources = ReadAppResources() };
        var classicContent = Assert.IsAssignableFrom<FrameworkElement>(classic.Content);
        Layout(classicContent, 800, 500);
        Assert.Contains(Descendants<ScrollViewer>(classicContent), ScreenshotViewportBehavior.GetEnabled);
        foreach (var newTheme in new[] { false, true })
        {
            PdaThemeSelector.UseNewTheme = newTheme;
            var pda = new PdaScreenshotsView { Resources = ReadAppResources(), DataContext = pdaViewModel };
            Layout(pda, 800, 500);
            Assert.Contains(Descendants<ScrollViewer>(pda), ScreenshotViewportBehavior.GetEnabled);
        }
    }

    private static void StorageMigrationWindowRenders()
    {
        var window = new StorageMigrationWindow(@"D:\CORDON\Data\CordonModLauncher",
            string.Join(Environment.NewLine, @"C:\Users\Player\AppData\Roaming\StalkerModLauncher", @"D:\StalkerModLauncher\Mods", @"D:\StalkerModLauncher\Workspaces", @"E:\StalkerModLauncher\Mods"));
        try
        {
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.ContentView.FindName("MigrationStatus")).Visibility);
            var content = Assert.IsType<Grid>(window.ContentView.Content);
            content.Measure(new Size(668, 550));
            content.Arrange(new Rect(0, 0, 668, 550));
            content.UpdateLayout();
            Assert.Equal(@"D:\CORDON\Data\CordonModLauncher", Assert.IsType<TextBlock>(window.ContentView.FindName("DestinationText")).Text);
            Assert.Contains(@"E:\StalkerModLauncher\Mods", Assert.IsType<TextBlock>(window.ContentView.FindName("SourcesText")).Text);
            var bitmap = new RenderTargetBitmap(668, 550, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(Path.GetTempPath(), "Cordon-storage-migration-preview.png"));
            encoder.Save(stream);
            window.Dispatcher.BeginInvoke(new Action(() =>
                Assert.IsType<Button>(window.ContentView.FindName("ProfilesOnlyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.True(window.ShowDialog());
            var decline = new StorageMigrationWindow("destination", "sources");
            decline.Dispatcher.BeginInvoke(new Action(() =>
                Assert.IsType<Button>(decline.ContentView.FindName("DeclineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.False(decline.ShowDialog());
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busy = new StorageMigrationWindow("destination", "sources", progress =>
            {
                progress.Report((0.4, "fixture.dat"));
                return completion.Task;
            });
            busy.Dispatcher.BeginInvoke(new Action(() =>
            {
                Assert.IsType<Button>(busy.ContentView.FindName("ProfilesOnlyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                busy.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Assert.False(Assert.IsType<ProgressBar>(busy.ContentView.FindName("MigrationProgressBar")).IsIndeterminate);
                    Assert.Equal(0.4, Assert.IsType<ProgressBar>(busy.ContentView.FindName("MigrationProgressBar")).Value);
                    var bar = Assert.IsType<ProgressBar>(busy.ContentView.FindName("MigrationProgressBar"));
                    bar.UpdateLayout();
                    var indicator = Assert.IsType<Border>(bar.Template.FindName("PART_Indicator", bar));
                    Assert.InRange(indicator.ActualWidth, 1, bar.ActualWidth - 1);
                    Assert.False(Assert.IsType<Button>(busy.ContentView.FindName("DeclineButton")).IsEnabled);
                    Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(busy.ContentView.FindName("MigrationStatus")).Visibility);
                    Assert.Contains("fixture.dat", Assert.IsType<TextBlock>(busy.ContentView.FindName("MigrationStatus")).Text);
                    busy.Close();
                    Assert.True(busy.IsVisible);
                    completion.SetResult();
                }));
            }));
            Assert.True(busy.ShowDialog());
            var attempts = 0;
            var errors = new TestDialogs();
            var retry = new StorageMigrationWindow("destination", "sources", _ =>
                ++attempts == 1 ? Task.FromException(new IOException("fixture failure")) : Task.CompletedTask, errors);
            retry.Dispatcher.BeginInvoke(new Action(() =>
            {
                var button = Assert.IsType<Button>(retry.ContentView.FindName("ProfilesOnlyButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(button.IsEnabled);
                Assert.Contains("fixture failure", errors.Error);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Assert.True(retry.ShowDialog());
            Assert.Equal(2, attempts);
        }
        finally { window.Close(); }
    }

    private static async Task StorageMigrationPageBlocksNavigationDuringTransferAsync()
    {
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new PdaMainView { Resources = ReadAppResources() };
        var page = new StorageMigrationView("destination", "sources", _ => copying.Task);
        page.Completed += (_, _) => completed.TrySetResult();
        shell.ShowPage(page, Strings.Settings_MigrationWizard, lifetime: page);
        Assert.IsType<Button>(page.FindName("ProfilesOnlyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(shell.IsMigrationActive);
        Assert.IsType<Button>(shell.FindName("ProfilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(shell.FindName("ProfileDrawer")).Visibility);
        shell.ShowProfilePage();
        Assert.Same(page, shell.CurrentPage);
        shell.ShowPage(new Grid(), "other page");
        Assert.Same(page, shell.CurrentPage);
        copying.SetResult();
        await completed.Task;
        shell.ShowProfilePage();
        Assert.Null(shell.CurrentPage);
    }

    private static void ProfileCreationRendersAndNavigates(string theme)
    {
        PdaThemeSelector.UseNewTheme = theme == "pda-new";
        var root = Path.Combine(Path.GetTempPath(), "ProfileCreationUi", Guid.NewGuid().ToString("N"));
        var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        File.WriteAllText(Path.Combine(game, "bin", "xr_3da.exe"), "fixture");
        File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true | false | $fs_root$ | appdata");
        var vm = new ProfileCreationViewModel { GamePath = game };
        var classic = theme == "classic" ? new ProfileCreationWindow(vm) : null;
        FrameworkElement view = classic is not null
            ? (FrameworkElement)classic.Content
            : new PdaProfileCreationView { DataContext = vm };
        if (classic is not null) { classic.Content = null; view.DataContext = vm; }
        var host = new Border { Resources = ReadAppResources(), Child = view };
        try
        {
            Layout(host, 720, 480);
            vm.NextCommand.Execute(null);
            Layout(host, 720, 480);
            var scan = Assert.Single(Descendants<Button>(view), button => ReferenceEquals(button.Command, vm.ScanModsCommand));
            Assert.Equal(Visibility.Visible, scan.Visibility);
            Assert.True(scan.IsEnabled);
            vm.NextCommand.Execute(null);
            Layout(host, 720, 480);
            Assert.True(vm.IsStepThree);
            Assert.True(vm.IsFinishVisible);
            Assert.Contains(Path.Combine(game, "fsgame.ltx"), vm.FsgameSourceDisplay);
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text == vm.GameDataDescription);
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text == vm.FsgameSourceDisplay);
            var browse = Assert.Single(Descendants<Button>(view), button => ReferenceEquals(button.Command, vm.BrowseFsgameCommand));
            var scroll = Assert.Single(Descendants<ScrollViewer>(view), item => item.Content is StackPanel panel && Descendants<Button>(panel).Contains(browse));
            scroll.ScrollToEnd();
            Layout(host, 720, 480);
            var bounds = browse.TransformToAncestor(host).TransformBounds(new Rect(browse.RenderSize));
            Assert.InRange(bounds.Bottom, 0, host.RenderSize.Height);
            var bitmap = new RenderTargetBitmap(720, 480, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var shared = Assert.Single(Descendants<RadioButton>(view), radio => Equals(radio.Content, Strings.SettingsProfile_UserDataGame));
            shared.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            Assert.True(vm.UseBaseGameData);
            shared.ApplyTemplate();
            var inner = Assert.IsType<System.Windows.Shapes.Ellipse>(shared.Template.FindName(theme == "classic" ? "InnerCircle" : "Inner", shared));
            Assert.Equal(Visibility.Visible, inner.Visibility);
            Assert.Equal(Assert.IsType<SolidColorBrush>(shared.FindResource("AccentBrush")).Color, Assert.IsType<SolidColorBrush>(inner.Fill).Color);
            Assert.Equal(Path.Combine(game, "appdata"), vm.GameDataDescription);
            if (classic is null)
            {
                ModProfile? result = null;
                vm.Completed += (_, profile) => result = profile;
                vm.FinishCommand.Execute(null);
                Assert.True(Assert.IsType<ModProfile>(result).UseBaseGameData);
            }
        }
        finally
        {
            host.Child = null;
            classic?.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LauncherSettingsImportButtonRendersAndRunsAsync(string theme)
    {
        PdaThemeSelector.UseNewTheme = theme == "pda-new";
        var calls = 0;
        var downloads = 0;
        var settings = new LauncherSettingsViewModel(LauncherPreferences.Default, "test", _ => Task.CompletedTask,
            new DialogService(),
            checkForUpdates: () => Task.FromResult(new LauncherUpdateResult("1.0.0", "v1.5.5",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.5.5", true)),
            downloadReleasePackage: (_, _) =>
            {
                downloads++;
                return Task.FromResult(@"C:\CORDON\CORDON-v1.5.5-win-x64.zip");
            },
            confirmInstall: _ => true,
            openMigrationWizard: () => calls++);
        var view = new LauncherSettingsView { DataContext = settings };
        var host = new Border { Resources = ReadAppResources(), Child = view };
        if (theme != "classic") host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(theme == "pda-new" ? "/CORDON;component/Themes/NewPdaTheme.xaml" : "/CORDON;component/Themes/PdaTheme.xaml", UriKind.Relative)
        });
        Layout(host, 760, 500);
        var install = Assert.IsType<Button>(view.FindName("DownloadAndInstallButton"));
        Assert.Equal(Visibility.Collapsed, install.Visibility);
        await settings.CheckForUpdatesAsync();
        Layout(host, 760, 500);
        Assert.Equal(Visibility.Visible, install.Visibility);
        Assert.Equal(Strings.Settings_DownloadAndInstall, install.Content);
        Assert.Same(settings.DownloadAndInstallCommand, install.Command);
        Assert.Single(Descendants<Button>(view), item => ReferenceEquals(item.Command, settings.DownloadAndInstallCommand));
        Assert.Null(view.FindName("DownloadOptionsPanel"));
        install.Command.Execute(null);
        Assert.Equal(1, downloads);
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.InRange(install.Opacity, 0.99, 1);
        Descendants<ScrollViewer>(view).First().ScrollToBottom();
        Layout(host, 760, 500);
        SavePreview(host, "settings-" + theme);
        var button = Assert.Single(Descendants<Button>(view), item => ReferenceEquals(item.Command, settings.OpenMigrationWizardCommand));
        Assert.True(button.IsEnabled);
        button.Command!.Execute(null);
        Assert.Equal(1, calls);
        Assert.Equal(Strings.Settings_MigrationWizard, button.Content);
        bool? selected = null;
        var migration = new StorageMigrationView("destination", "sources", _ =>
        {
            selected = true;
            return Task.CompletedTask;
        });
        if (theme == "classic") host.Child = migration;
        else
        {
            host.Child = null;
            var shell = new PdaMainView { DataContext = new { UseNewPdaInterface = theme == "pda-new" } };
            shell.ShowPage(migration, Strings.Settings_MigrationWizard);
            host.Child = shell;
            Assert.Same(migration, shell.CurrentPage);
        }
        Layout(host, theme == "classic" ? 760 : 1200, theme == "classic" ? 500 : 823);
        var bar = Assert.IsType<ProgressBar>(migration.FindName("MigrationProgressBar"));
        if (theme == "pda")
        {
            Assert.Same(bar.FindResource("ModArchiveProgressFillBrush"), bar.Foreground);
            Assert.Same(bar.FindResource("ModArchiveProgressTrackBrush"), bar.Background);
            Assert.Same(bar.FindResource("StrokeBrush"), bar.BorderBrush);
        }
        else Assert.Equal(Color.FromRgb(0xD1, 0x8B, 0x22), Assert.IsType<SolidColorBrush>(bar.Foreground).Color);
        Assert.True(Assert.IsType<Button>(migration.FindName("ProfilesOnlyButton")).IsEnabled);
        Assert.Null(migration.FindName("TransferButton"));
        Assert.Equal(Strings.Migration_ProfilesOnly, Assert.IsType<Button>(migration.FindName("ProfilesOnlyButton")).Content);
        Assert.IsType<Button>(migration.FindName("ProfilesOnlyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(selected);
        Assert.Equal(1, Assert.IsType<ProgressBar>(migration.FindName("MigrationProgressBar")).Value);
    }

    private static void WizardRendersNavigatesAndInstallsThroughBoundControls(string theme)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
        PdaThemeSelector.UseNewTheme = theme == "pda-new";
        using var wizard = CreateWizard();
        var view = new FomodInstallerContentView { DataContext = wizard };
        var host = new Border { Resources = ReadAppResources(), Child = view };
        view.UsePdaTheme = theme != "classic";
        Layout(host, theme == "classic" ? 920 : 880, 530);

        var radios = Descendants<RadioButton>(view).Where(button => button.Visibility == Visibility.Visible).ToArray();
        Assert.Equal(2, radios.Length);
        radios[1].SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        Assert.True(wizard.CurrentStep!.Groups[0].Options[1].IsSelected);
        Assert.False(wizard.CurrentStep.Groups[0].Options[0].IsSelected);
        Assert.Same(wizard.CurrentStep.Groups[0].Options[1], wizard.PreviewOption);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Layout(host, theme == "classic" ? 920 : 880, 530);
        Assert.False(radios[0].IsChecked);
        Assert.True(radios[1].IsChecked);
        foreach (var card in Descendants<Border>(view).Where(border => ReferenceEquals(border.Style, view.Resources["FomodOptionCard"])))
        {
            var option = Assert.IsType<FomodInstallerOption>(card.DataContext);
            Assert.Same(view.FindResource(option.IsSelected ? "AccentGreenBrush" : "StrokeBrush"), card.BorderBrush);
        }
        SavePreview(host, theme);
        var next = Assert.Single(Descendants<Button>(view), button => ReferenceEquals(button.Command, wizard.NextCommand));
        Assert.True(next.IsVisible || next.Visibility == Visibility.Visible);
        next.Command!.Execute(null);
        Assert.Equal(1, wizard.CurrentStepIndex);
        Layout(host, 880, 530);
        var checkBoxes = Descendants<CheckBox>(view).Where(button => button.Visibility == Visibility.Visible).ToArray();
        Assert.Equal(2, checkBoxes.Length);
        foreach (var checkBox in checkBoxes)
            checkBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Layout(host, 880, 530);
        Assert.All(wizard.CurrentStep!.Groups[0].Options, option => Assert.True(option.IsSelected));
        Assert.All(checkBoxes, checkBox =>
        {
            Assert.True(checkBox.IsChecked);
            var mark = Assert.IsType<Image>(checkBox.Template.FindName("Mark", checkBox));
            Assert.Same(view.FindResource("RadiationOnImage"), mark.Source);
        });
        checkBoxes[0].SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
        Assert.False(wizard.CurrentStep.Groups[0].Options[0].IsSelected);
        Assert.True(wizard.CurrentStep.Groups[0].Options[1].IsSelected);
        checkBoxes[0].SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        wizard.BackCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        Assert.All(wizard.CurrentStep!.Groups[0].Options, option => Assert.True(option.IsSelected));
        var install = Assert.Single(Descendants<Button>(view), button => ReferenceEquals(button.Command, wizard.InstallCommand));
        Assert.Equal(Visibility.Visible, install.Visibility);
        install.Command!.Execute(null);
        Assert.True(wizard.Completion.IsCompletedSuccessfully);
        var selection = wizard.Completion.Result;
        Assert.NotNull(selection);
        Assert.True(selection.IsSelected(0, 0, 1));
        Assert.True(selection.IsSelected(1, 0, 0));
        Assert.True(selection.IsSelected(1, 0, 1));
        Assert.Equal(3, FomodSelector.ResolveFiles(CreateModule(), selection).Count);
    }

    private static void ClassicWindowCloseCancelsAndPdaPageReplacementCancels()
    {
        using var classicWizard = CreateWizard();
        var window = new FomodInstallerWindow(classicWizard) { Resources = ReadAppResources() };
        Assert.IsType<FomodInstallerContentView>(window.Content);
        window.Close();
        Assert.True(classicWizard.Completion.IsCompletedSuccessfully);
        Assert.Null(classicWizard.Completion.Result);

        using var pdaWizard = CreateWizard();
        var shell = new PdaMainView { Resources = ReadAppResources() };
        var content = new FomodInstallerContentView { DataContext = pdaWizard };
        shell.ShowPage(content, Strings.Fomod_Title, lifetime: pdaWizard);
        content.UsePdaTheme = true;
        Layout(shell, 1200, 823);
        shell.ShowProfilePage();
        Assert.True(pdaWizard.Completion.IsCompletedSuccessfully);
        Assert.Null(pdaWizard.Completion.Result);
    }

    private static async Task FolderRequestsUseExistingWizardForBothInterfacesAsync(Application application)
    {
        var root = Path.Combine(Path.GetTempPath(), "FomodProfileUi", Guid.NewGuid().ToString("N"));
        var dialogs = new TestDialogs();
        var model = MainViewModelTests.CreateViewModel(root, dialogs);
        var owner = new Window();
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        application.MainWindow = owner;
        try
        {
            await MainViewModelTests.WaitForSettingsLoadedAsync(model);
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var profile = new ModProfile { Name = "Folder installation", GameInstallPath = game, ModInstallPath = Path.Combine(root, "installed") };
            model.AddCreatedProfile(profile);
            var plain = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
            await model.AddModFolderAsync(plain);
            Assert.Equal(plain, Assert.Single(profile.Mods).SourcePath);
            Assert.False(Directory.Exists(profile.ModInstallPath));

            model.IsPdaInterfaceEnabled = true;
            EventHandler<FomodInstallerViewModel> accept = (_, wizard) =>
            {
                Assert.True(model.IsBuilding);
                Assert.False(model.AddModCommand.CanExecute(null));
                wizard.CurrentStep!.Groups[0].Options[1].IsSelected = true;
                wizard.InstallCommand.Execute(null);
            };
            model.FomodInstallRequested += accept;
            var source = CreateFolderPackage(root, "pda-folder");
            await model.AddModFolderAsync(source);
            Assert.True(dialogs.Error is null, dialogs.Error);
            Assert.Equal(2, profile.Mods.Count);
            var installed = profile.Mods[1].SourcePath;
            Assert.NotEqual(source, installed);
            Assert.Equal("B", File.ReadAllText(Path.Combine(installed, "gamedata/chosen.txt")));
            Assert.Equal("A", File.ReadAllText(Path.Combine(source, "a.txt")));
            Assert.False(model.IsBuilding);
            Assert.True(model.IsModArchiveInstallCompleted);
            Assert.Empty(application.Windows.OfType<FomodInstallerWindow>());

            var dropped = CreateFolderPackage(root, "dropped-folder");
            await model.AddDroppedModsAsync([dropped, dropped]);
            Assert.Equal(3, profile.Mods.Count);
            Assert.NotEqual(dropped, profile.Mods[2].SourcePath);
            model.FomodInstallRequested -= accept;

            EventHandler<FomodInstallerViewModel> cancel = (_, wizard) => wizard.CancelCommand.Execute(null);
            model.FomodInstallRequested += cancel;
            var cancelled = CreateFolderPackage(root, "cancelled-folder");
            await model.AddModFolderAsync(cancelled);
            Assert.Equal(3, profile.Mods.Count);
            Assert.False(Directory.Exists(Path.Combine(profile.ModInstallPath, "cancelled-folder")));
            Assert.True(File.Exists(Path.Combine(cancelled, "fomod/ModuleConfig.xml")));
            Assert.False(model.IsInstallingModArchive);
            model.FomodInstallRequested -= cancel;

            model.IsPdaInterfaceEnabled = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var window = application.Windows.OfType<FomodInstallerWindow>().FirstOrDefault();
                if (window?.DataContext is not FomodInstallerViewModel wizard || !window.IsVisible) return;
                timer.Stop();
                wizard.InstallCommand.Execute(null);
            };
            timer.Start();
            try { await model.AddModFolderAsync(CreateFolderPackage(root, "classic-folder")); }
            finally { timer.Stop(); }
            Assert.True(dialogs.Error is null, dialogs.Error);
            Assert.Equal(4, profile.Mods.Count);
            Assert.Equal("A", File.ReadAllText(Path.Combine(profile.Mods[3].SourcePath, "gamedata/chosen.txt")));
            Assert.False(model.IsBuilding);
        }
        finally
        {
            await model.CleanupAsync();
            owner.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestDialogs : DialogService
    {
        public string? Error { get; private set; }
        public override void ShowError(string title, string message) => Error = message;
    }

    private static string CreateFolderPackage(string root, string name)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(source, "fomod"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        File.WriteAllText(Path.Combine(source, "b.txt"), "B");
        File.WriteAllText(Path.Combine(source, "fomod/ModuleConfig.xml"), """
            <config><moduleName>M</moduleName><installSteps order="Explicit"><installStep name="S">
            <optionalFileGroups><group name="G" type="SelectExactlyOne"><plugins order="Explicit">
            <plugin name="A"><files><file source="a.txt" destination="gamedata/chosen.txt"/></files><typeDescriptor><type name="Recommended"/></typeDescriptor></plugin>
            <plugin name="B"><files><file source="b.txt" destination="gamedata/chosen.txt"/></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
            </plugins></group></optionalFileGroups></installStep></installSteps></config>
            """);
        return source;
    }

    [Fact]
    public void InvalidImageDoesNotCrashOrHoldPackageFileOpen()
    {
        RunSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllText(path, "not an image");
                var converter = new FomodImageConverter();
                Assert.Same(DependencyProperty.UnsetValue, converter.Convert(path, typeof(ImageSource), null, CultureInfo.InvariantCulture));
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ValidImageIsDecodedWithoutKeepingStagingOpen()
    {
        RunSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 0, 100, 0, 255, 0, 100, 0, 255, 0, 100, 0, 255, 0, 100, 0, 255 }, 8);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path)) encoder.Save(stream);
                var result = new FomodImageConverter().Convert(path, typeof(ImageSource), null, CultureInfo.InvariantCulture);
                Assert.True(Assert.IsType<BitmapImage>(result).IsFrozen);
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            finally { File.Delete(path); }
        });
    }

    private static FomodInstallerViewModel CreateWizard() => new(CreateModule(), Path.GetTempPath());

    [Theory]
    [InlineData(2, 2, 2, 2)]
    [InlineData(10, 1200, 4, 480)]
    [InlineData(1200, 10, 480, 4)]
    [InlineData(1200, 800, 480, 320)]
    [InlineData(800, 1200, 320, 480)]
    public void ImageDecodeBoundsBothDimensionsWithoutUpscaling(int width, int height, int expectedWidth, int expectedHeight)
    {
        RunSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                    new byte[width * height * 4], width * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = File.Create(path)) encoder.Save(stream);
                var result = Assert.IsType<BitmapImage>(new FomodImageConverter().Convert(path, typeof(ImageSource), null, CultureInfo.InvariantCulture));
                Assert.Equal(expectedWidth, result.PixelWidth);
                Assert.Equal(expectedHeight, result.PixelHeight);
                Assert.True(result.IsFrozen);
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            finally { File.Delete(path); }
        });
    }

    private static FomodModule CreateModule() => new("Atmosphere & Textures", [],
    [
        new("Качество текстур", null, [new("Разрешение", FomodGroupType.SelectExactlyOne,
        [
            new("2K — сбалансированный", "Подходит для большинства видеокарт. Сохраняет атмосферу Зоны и умеренное потребление памяти.",
                FomodPluginType.Recommended, [], [new("2k", "gamedata", 0)], new Dictionary<string, string>()),
            new("4K — максимальная детализация", "Детализированные поверхности, оружие и окружение. Рекомендуется видеокарта с 8 ГБ видеопамяти или больше.",
                FomodPluginType.Optional, [], [new("4k", "gamedata", 0)], new Dictionary<string, string>())
        ])]),
        new("Дополнения", null, [new("Необязательные компоненты", FomodGroupType.SelectAny,
        [
            new("Обновлённые звуки окружения", "Звуки ветра и аномалий.", FomodPluginType.Optional, [], [new("sound", "gamedata/sounds", 0)], new Dictionary<string, string>()),
            new("Новые иконки", "Иконки предметов.", FomodPluginType.Optional, [], [new("icons", "gamedata/textures", 0)], new Dictionary<string, string>())
        ])])
    ], []);

    private static ResourceDictionary ReadAppResources()
    {
        var project = FindProjectRoot();
        var document = XDocument.Load(Path.Combine(project, "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = document.Root!.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "res", "clr-namespace:StalkerModLauncher.Resources;assembly=CORDON");
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString(), new ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/CORDON;component/App.xaml")
        });
    }

    private static void Layout(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static void SavePreview(FrameworkElement view, string name)
    {
        var directory = Environment.GetEnvironmentVariable("FOMOD_SMOKE_OUTPUT");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "fomod-" + name + ".png"));
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "StalkerModLauncher");
            if (File.Exists(Path.Combine(candidate, "StalkerModLauncher.csproj"))) return candidate;
        }
        throw new DirectoryNotFoundException("Project root not found.");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

[CollectionDefinition("Fomod UI", DisableParallelization = true)]
public sealed class FomodUiGroup;
