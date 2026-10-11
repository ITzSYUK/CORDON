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
                    WizardRendersNavigatesAndInstallsThroughBoundControls("classic");
                    WizardRendersNavigatesAndInstallsThroughBoundControls("pda");
                    WizardRendersNavigatesAndInstallsThroughBoundControls("pda-new");
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
