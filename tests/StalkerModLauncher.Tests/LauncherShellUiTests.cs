using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using StalkerModLauncher.Services;
using StalkerModLauncher.Themes;
using StalkerModLauncher.Views;
using StalkerModLauncher.Views.Controls;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LauncherShellUiTests
{
    [Fact]
    public void LauncherSettingsRendersAtClassicAndPdaContentSize()
    {
        var settings = LoadProjectXaml("Views", "Controls", "LauncherSettingsView.xaml");
        var classicWindow = LoadProjectXaml("Views", "LauncherSettingsWindow.xaml");

        Assert.DoesNotContain("ПОВЕДЕНИЕ", settings.ToString());
        Assert.Contains("FontSize=\"12\"", settings.ToString());
        Assert.Contains("Width=\"20\" Height=\"20\"", settings.ToString());
        Assert.Contains("Strings.Settings_LogLevelLabel", settings.ToString());
        Assert.Contains("Strings.Settings_StorageLabel", settings.ToString());
        Assert.Contains("TextWrapping=\"NoWrap\"", settings.ToString());
        Assert.Contains("Strings.Settings_UpdateHeading", settings.ToString());
        Assert.Contains("Strings.Settings_CheckUpdates", settings.ToString());
        Assert.Contains("CheckForUpdatesCommand", settings.ToString());
        Assert.Contains("Strings.Settings_OpenRelease", settings.ToString());
        Assert.Contains("OpenReleaseButton", settings.ToString());
        Assert.Contains("Strings.Settings_DownloadAndInstall", settings.ToString());
        Assert.Contains("DownloadToDownloadsButton", settings.ToString());
        Assert.Contains("Strings.Settings_OpenLauncherFolder", settings.ToString());
        Assert.Contains("Strings.Settings_MinimalVersion", settings.ToString());
        Assert.Contains("Strings.Settings_StandaloneVersion", settings.ToString());
        Assert.Contains("Strings.Settings_AutoCheckUpdates", settings.ToString());
        Assert.Contains("Strings.Settings_StartMinimized", settings.ToString());
        Assert.Contains("StartMinimizedToTrayOnWindowsStartup", settings.ToString());
        Assert.Contains("Strings.Settings_ShowTrayIcon", settings.ToString());
        Assert.Contains("CanStartMinimizedToTray", settings.ToString());
        Assert.Contains("Strings.Settings_ShowUpdateNotifications", settings.ToString());
        Assert.Contains("ShowUpdateNotifications", settings.ToString());
        Assert.Contains("Strings.Settings_Reset", settings.ToString());
        Assert.Contains("ResetCommand", settings.ToString());
        Assert.Contains("LauncherSettingsPanelCornerRadius", settings.ToString());
        Assert.Contains("Strings.Settings_LogDescriptionDetailed", settings.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Только ошибки —", settings.ToString());
        Assert.Equal("760", (string?)classicWindow.Root?.Attribute("Width"));

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var behaviorOptions = settings.Descendants(presentation + "CheckBox").ToList();
        var startWithWindowsIndex = behaviorOptions.FindIndex(element =>
            (string?)element.Attribute("Content") == "{x:Static res:Strings.Settings_StartWithWindows}");
        var showTrayIconIndex = behaviorOptions.FindIndex(element =>
            (string?)element.Attribute("Content") == "{x:Static res:Strings.Settings_ShowTrayIcon}");
        Assert.True(startWithWindowsIndex < showTrayIconIndex);
        var minimizeToTray = Assert.Single(behaviorOptions, element =>
            (string?)element.Attribute("Content") == "{x:Static res:Strings.Settings_MinimizeToTray}");
        Assert.Equal("26,0,0,0", (string?)minimizeToTray.Attribute("Margin"));
        Assert.Equal("{Binding CanUseTray}", (string?)minimizeToTray.Attribute("IsEnabled"));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = CreateThemeHost();
                var classicSettings = new LauncherSettingsView();
                host.Children.Add(classicSettings);
                host.Measure(new Size(600, 480));
                host.Arrange(new Rect(0, 0, 600, 480));
                host.UpdateLayout();

                var classicPanel = Assert.IsType<Border>(classicSettings.FindName("SettingsPanelBorder"));
                Assert.Equal(new CornerRadius(5), classicPanel.CornerRadius);

                var bitmap = new RenderTargetBitmap(600, 480, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                Assert.Equal(600, bitmap.PixelWidth);

                var pda = CreatePdaThemeHost();
                var pdaSettings = new LauncherSettingsView();
                pda.Children.Add(pdaSettings);
                pda.Measure(new Size(908, 521));
                pda.Arrange(new Rect(0, 0, 908, 521));
                pda.UpdateLayout();

                var pdaPanel = Assert.IsType<Border>(pdaSettings.FindName("SettingsPanelBorder"));
                Assert.Equal(new CornerRadius(0), pdaPanel.CornerRadius);

                var pdaBitmap = new RenderTargetBitmap(908, 521, 96, 96, PixelFormats.Pbgra32);
                pdaBitmap.Render(pda);
                Assert.Equal(908, pdaBitmap.PixelWidth);

                var originalAtlas = new FormatConvertedBitmap(
                    PdaThemeSelector.CurrentDrawerAtlas,
                    PixelFormats.Bgra32,
                    null,
                    0);
                PdaThemeSelector.UseNewTheme = true;
                var neutralAtlas = PdaThemeSelector.CurrentDrawerAtlas;
                Assert.Equal(PixelFormats.Bgra32, neutralAtlas.Format);
                var stride = neutralAtlas.PixelWidth * 4;
                var originalPixels = new byte[stride * originalAtlas.PixelHeight];
                var pixels = new byte[stride * neutralAtlas.PixelHeight];
                originalAtlas.CopyPixels(originalPixels, stride, 0);
                neutralAtlas.CopyPixels(pixels, stride, 0);
                Assert.Equal(
                    Enumerable.Range(0, neutralAtlas.PixelWidth * neutralAtlas.PixelHeight)
                        .Select(pixel => originalPixels[(pixel * 4) + 3]),
                    Enumerable.Range(0, neutralAtlas.PixelWidth * neutralAtlas.PixelHeight)
                        .Select(pixel => pixels[(pixel * 4) + 3]));

                var newPda = CreatePdaThemeHost(useNewTheme: true);
                newPda.Width = 959;
                newPda.Height = 424;
                var newPdaSettings = new LauncherSettingsView();
                newPda.Children.Add(newPdaSettings);
                newPda.Measure(new Size(959, 424));
                newPda.Arrange(new Rect(0, 0, 959, 424));
                newPda.UpdateLayout();

                var newPdaBitmap = new RenderTargetBitmap(959, 424, 96, 96, PixelFormats.Pbgra32);
                newPdaBitmap.Render(newPda);
                Assert.Equal(424, newPdaBitmap.PixelHeight);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                PdaThemeSelector.UseNewTheme = false;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void TrayPanelHidesBackendSelectionAndAnimatesPlayButton()
    {
        var trayDocument = LoadProjectXaml("Views", "TrayProfilePanel.xaml");
        var sidebarDocument = LoadProjectXaml("Views", "Controls", "ProfileSidebarView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var traySidebar = trayDocument.Descendants()
            .Single(element => element.Name.LocalName == "ProfileSidebarView");
        var trayStyle = sidebarDocument.Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(xaml + "Key") == "TrayProfileListBoxItemStyle");

        Assert.Equal("True", (string?)traySidebar.Attribute("IsTrayMode"));
        Assert.Empty(trayDocument.Descendants(presentation + "ComboBox"));
        Assert.DoesNotContain("SecondaryProfileActionCommand", trayStyle.ToString());
        Assert.Contains("PrimaryProfileActionCommand", trayStyle.ToString());
        Assert.DoesNotContain("Workspace", trayStyle.ToString());
        Assert.DoesNotContain("USVFS", trayStyle.ToString());
        Assert.Contains("Strings.Common_Launch", trayStyle.ToString());
        Assert.DoesNotContain("Запустить профиль", trayStyle.ToString());
        Assert.Contains(
            sidebarDocument.Descendants(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "MaxHeight" &&
                      (string?)setter.Attribute("Value") == "34");
        Assert.DoesNotContain("IsSelected", trayStyle.ToString());
        var profileList = sidebarDocument.Descendants(presentation + "ListBox")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "ProfilesList");
        Assert.Null(profileList.Attribute("SelectedItem"));
        Assert.Contains(
            sidebarDocument.Descendants(presentation + "DataTrigger")
                .Where(trigger => (string?)trigger.Attribute("Value") == "True")
                .SelectMany(trigger => trigger.Elements(presentation + "Setter")),
            setter => (string?)setter.Attribute("Property") == "SelectedItem" &&
                      (string?)setter.Attribute("Value") == "{x:Null}");
        Assert.Contains(
            trayStyle.Descendants(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Focusable" &&
                      (string?)setter.Attribute("Value") == "False");
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource TextBrush}\"", sidebarDocument.ToString());
        Assert.Contains(
            trayStyle.Descendants(presentation + "DoubleAnimation"),
            animation => (string?)animation.Attribute("Storyboard.TargetName") == "ProfileActions" &&
                         (string?)animation.Attribute("To") == "1" &&
                         (string?)animation.Attribute("Duration") == "0:0:0.18");
        var profileActionsExit = trayStyle.Descendants(presentation + "DoubleAnimation")
            .Single(animation =>
                (string?)animation.Attribute("Storyboard.TargetName") == "ProfileActions" &&
                (string?)animation.Attribute("Storyboard.TargetProperty") ==
                "(UIElement.RenderTransform).(TranslateTransform.X)" &&
                (string?)animation.Attribute("To") == "18");
        var exitDuration = (string?)profileActionsExit.Attribute("Duration");
        Assert.NotNull(exitDuration);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            TimeSpan.Parse(exitDuration!, CultureInfo.InvariantCulture));
        Assert.Equal("BitmapCache", (string?)trayDocument.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PanelRoot")
            .Attribute("CacheMode"));
        Assert.Equal("Window", trayDocument.Root?.Name.LocalName);
        Assert.Equal("None", (string?)trayDocument.Root?.Attribute("WindowStyle"));
        Assert.Equal("False", (string?)trayDocument.Root?.Attribute("ShowInTaskbar"));
        Assert.Equal("Window_OnDeactivated", (string?)trayDocument.Root?.Attribute("Deactivated"));
        Assert.Contains("HasLaunchError", sidebarDocument.ToString());
        Assert.Contains("LaunchErrorSummary", sidebarDocument.ToString());
        Assert.Contains("Text=\"!\"", sidebarDocument.ToString());
        Assert.Contains(
            trayStyle.Descendants(presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding HasLaunchError}" &&
                       (string?)trigger.Attribute("Value") == "True");
    }

    [Fact]
    public void AnomalyAutomaticRendererIsNamedLauncherInBothInterfaces()
    {
        var classic = LoadProjectXaml("Views", "ProfileSettingsWindow.xaml").ToString();
        var pda = LoadProjectXaml("Views", "Controls", "PdaProfileSettingsView.xaml").ToString();

        Assert.Contains("Strings.SettingsProfile_Launcher", classic);
        Assert.Contains("Strings.SettingsProfile_Launcher", pda);
        Assert.DoesNotContain("Content=\"Авто\"", classic);
        Assert.DoesNotContain("Content=\"Авто\"", pda);
    }

    [Fact]
    public void ProfileSettingsDoNotLabelBackendsAsStable()
    {
        var classic = LoadProjectXaml("Views", "ProfileSettingsWindow.xaml").ToString();
        var pda = LoadProjectXaml("Views", "Controls", "PdaProfileSettingsView.xaml").ToString();

        Assert.DoesNotContain("Workspace — стабильный", classic);
        Assert.DoesNotContain("USVFS — стабильный", classic);
        Assert.DoesNotContain("Workspace — стабильный", pda);
        Assert.DoesNotContain("USVFS — стабильный", pda);
        Assert.DoesNotContain("эксперимент", classic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("эксперимент", pda, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedModPanelUsesFlatVirtualizedCollapsibleGroups()
    {
        var panel = LoadProjectXaml("Views", "Controls", "ModPanelView.xaml").ToString();
        var code = LoadProjectText("Views", "Controls", "ModPanelView.xaml.cs");

        Assert.DoesNotContain("ListView.GroupStyle", panel);
        Assert.DoesNotContain("GroupItem", panel);
        Assert.DoesNotContain("RequestBringIntoView", panel);
        Assert.DoesNotContain("Strings.Mod_CreateGroup", panel);
        Assert.DoesNotContain("Strings.Mod_MoveToGroup", panel);
        Assert.Contains("const int visibleGroupCount = 5", code);
        Assert.Contains("PreviewMouseWheel", code);
        Assert.Contains("CreateLeftClickMenuItem(\"▲\"", code);
        Assert.Contains("CreateLeftClickMenuItem(\"▼\"", code);
        Assert.Contains("HorizontalContentAlignment = HorizontalAlignment.Center", code);
        Assert.Contains("Strings.Mod_MoveToGroupStart", code);
        Assert.Contains("Strings.Mod_MoveToGroupEnd", code);
        Assert.Contains("Strings.Mod_DisableGroup", code);
        Assert.Contains("SetModGroupEnabled(groupName, !groupEnabled)", code);
        Assert.Contains("MinimumHorizontalDragDistance &&", code);
        Assert.Contains("PreviewMouseMove=\"ModsList_OnMouseMove\"", panel);
        Assert.Contains("FindAncestor<Border>(source, \"ModGroupHeader\")", code);
        Assert.DoesNotContain("ModGroupHeader_OnPreviewMouseLeftButtonDown", panel);
        Assert.Contains("_dropTargetGroupHeader?.DataContext as ModEntry", code);
        Assert.Contains("var target = _dropTargetItem?.DataContext as ModEntry", code);
        Assert.Contains("RestoreSelection(payload.Mods, scrollIntoView: false)", code);
        Assert.DoesNotContain("_preserveScrollDuringDrop", code);
        Assert.DoesNotContain("RestoreScrollAfterDrop", code);
        Assert.DoesNotContain("_draggedGroupName", code);
        Assert.DoesNotContain("bool IsGroup", code);
        Assert.Contains("MoveModGroupByOffset(groupName, -1)", code);
        Assert.Contains("MoveModGroupByOffset(groupName, 1)", code);
        var modStyles = LoadProjectXaml("Themes", "MainWindowStyles.xaml").ToString();
        Assert.Contains("x:Name=\"ModGroupHeader\"", modStyles);
        Assert.Contains("x:Name=\"GroupToggleCircle\"", modStyles);
        Assert.Contains("Background=\"{DynamicResource AccentBrush}\"", modStyles);
        Assert.Contains("MinHeight=\"26\"", modStyles);
        Assert.Contains("Binding ShowsGroupHeader", modStyles);
        Assert.Contains("Binding IsGroupCollapsed", modStyles);
        Assert.Contains("x:Name=\"GroupGuide\"", modStyles);
        Assert.Contains("Binding ViewGroupKey.IsGroup", modStyles);
        Assert.Contains("TargetName=\"RowContent\" Property=\"Margin\" Value=\"22,4,4,4\"", modStyles);
        Assert.Contains(
            "HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\"",
            LoadProjectXaml("App.xaml").ToString());
        Assert.Contains(
            "HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\"",
            LoadProjectXaml("Themes", "PdaTheme.xaml").ToString());
    }

    [Fact]
    public void GroupNamePromptUsesClassicDialogAndPdaInlinePanel()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dialog = LoadProjectXaml("Views", "TextPromptWindow.xaml");
        var dialogButtons = dialog.Descendants(presentation + "Button").ToList();
        var cancel = Assert.Single(dialogButtons, button =>
            (string?)button.Attribute("Content") == "{x:Static res:Strings.Common_Cancel}");
        var done = Assert.Single(dialogButtons, button =>
            (string?)button.Attribute("Content") == "{x:Static res:Strings.Common_Done}");

        Assert.True(dialogButtons.IndexOf(cancel) < dialogButtons.IndexOf(done));
        Assert.Equal("{StaticResource PrimaryButtonStyle}", (string?)done.Attribute("Style"));
        Assert.Equal("220", (string?)dialog.Root?.Attribute("Height"));

        var panel = LoadProjectXaml("Views", "Controls", "ModPanelView.xaml");
        var inlinePrompt = Assert.Single(panel.Descendants(presentation + "Border"), element =>
            (string?)element.Attribute(xaml + "Name") == "GroupNamePromptPanel");
        Assert.Equal("{DynamicResource ModArchiveProgressPanelStyle}", (string?)inlinePrompt.Attribute("Style"));
        Assert.Equal("Collapsed", (string?)inlinePrompt.Attribute("Visibility"));
        Assert.Equal("2", (string?)inlinePrompt.Attribute("Grid.Row"));
        Assert.Equal("0,8,0,0", (string?)inlinePrompt.Attribute("Margin"));
        Assert.Null(inlinePrompt.Attribute("MaxWidth"));
        Assert.DoesNotContain("GroupNamePromptTitle", panel.ToString());

        var pdaMainDocument = LoadProjectXaml("Views", "Controls", "PdaMainView.xaml");
        var pdaMain = pdaMainDocument.ToString();
        var inlineError = Assert.Single(pdaMainDocument.Descendants(presentation + "Border"), element =>
            (string?)element.Attribute(xaml + "Name") == "InlineErrorPanel");
        Assert.Equal("18,10", (string?)inlineError.Attribute("Margin"));
        var code = LoadProjectText("Views", "Controls", "ModPanelView.xaml.cs");
        Assert.Contains("UsePdaTheme=\"True\"", pdaMain);
        Assert.Contains("if (!UsePdaTheme)", code);
        Assert.DoesNotContain("InputBox", LoadProjectText("Services", "DialogService.cs"));
    }

    [Fact]
    public void ProfileSettingsPlaceUserDataBetweenRendererAndExecutableWithLocalRadioGroups()
    {
        var interfaces = new[]
        {
            LoadProjectXaml("Views", "ProfileSettingsWindow.xaml").ToString(),
            LoadProjectXaml("Views", "Controls", "PdaProfileSettingsView.xaml").ToString()
        };

        foreach (var xaml in interfaces)
        {
            var renderer = xaml.IndexOf("Strings.SettingsProfile_AnomalyEngine", StringComparison.Ordinal);
            var userData = xaml.IndexOf("Strings.SettingsProfile_UserData", StringComparison.Ordinal);
            var executable = xaml.IndexOf("Strings.SettingsProfile_Executable", StringComparison.Ordinal);

            Assert.True(renderer >= 0 && renderer < userData && userData < executable);
            Assert.DoesNotContain("GroupName=", xaml);
            Assert.DoesNotContain("Text=\"Данные игры\"", xaml);
        }
    }

    [Fact]
    public void ProfileSettingsExposeManualFsgameSelectionInBothInterfaces()
    {
        var interfaces = new[]
        {
            LoadProjectXaml("Views", "ProfileSettingsWindow.xaml").ToString(),
            LoadProjectXaml("Views", "Controls", "PdaProfileSettingsView.xaml").ToString()
        };

        Assert.All(interfaces, xaml =>
        {
            Assert.Contains("Strings.SettingsProfile_FsgameSource", xaml);
            Assert.Contains("BrowseFsgameCommand", xaml);
            Assert.Contains("ClearFsgameSourceCommand", xaml);
        });
    }

    [Fact]
    public void TrayPopupCanBeReopenedImmediately()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayProfilePanel? panel = null;
            try
            {
                panel = new TrayProfilePanel();
                panel.ShowNearTray();
                Assert.True(panel.IsPanelOpen);
                panel.HidePanel();
                Assert.False(panel.IsPanelOpen);
                Assert.True(panel.WasRecentlyHidden);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    panel.ShowNearTray();
                    Assert.True(panel.IsPanelOpen);
                    panel.HidePanel();
                    Assert.False(panel.IsPanelOpen);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                panel?.HidePanel();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void TrayProfileRowsBlockSelectionInput()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var sidebar = new ProfileSidebarView { IsTrayMode = true };
                var profiles = Assert.IsType<ListBox>(sidebar.FindName("ProfilesList"));
                var profileItem = new ListBoxItem();
                var input = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = profileItem
                };

                profiles.RaiseEvent(input);

                Assert.True(input.Handled);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void BothInterfacesExposeLauncherSettingsButton()
    {
        var classic = LoadProjectXaml("Views", "Controls", "ProfileSidebarView.xaml");
        var pda = LoadProjectXaml("Views", "Controls", "PdaMainView.xaml");
        var pdaWindow = LoadProjectXaml("Views", "PdaWindow.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("LauncherSettingsButton_OnClick", classic.ToString());
        Assert.Contains("LauncherSettingsButton_OnClick", pda.ToString());
        Assert.Contains("/CORDON;component/Resources/PdaShell.HD.png", pda.ToString());
        Assert.Contains("/CORDON;component/Resources/AltPdaShell.png", pda.ToString());
        Assert.Contains("86,86,135,94", pda.ToString());
        var newPdaClip = Assert.Single(pda.Descendants(), element =>
            (string?)element.Attribute(xaml + "Key") == "NewPdaScreenClip");
        Assert.Equal("0,0,1083,597", (string?)newPdaClip.Attribute("Rect"));
        Assert.Equal("17", (string?)newPdaClip.Attribute("RadiusX"));
        Assert.Equal("17", (string?)newPdaClip.Attribute("RadiusY"));
        Assert.Contains("Property=\"Width\" Value=\"1304\"", pdaWindow.ToString());
        Assert.Contains("Property=\"Height\" Value=\"777\"", pdaWindow.ToString());
        Assert.DoesNotContain("PdaSystemSettingsButtonStyle", pda.ToString());
        Assert.Equal(1, pda.Descendants()
            .Count(element => (string?)element.Attribute("Style") == "{StaticResource PdaCompactTopTabStyle}"));
        Assert.DoesNotContain("ToggleInterfaceCommand", pda.ToString());
        Assert.DoesNotContain("Вернуться к обычному интерфейсу", pda.ToString());
        Assert.Equal("3", (string?)pda.Descendants()
            .Single(element => (string?)element.Attribute("Click") == "PowerButton_OnClick")
            .Attribute("Grid.Column"));
        Assert.Equal(1, pda.Descendants()
            .Count(element => (string?)element.Attribute("Value") == "{StaticResource PdaPowerNormalBrush}"));
        Assert.DoesNotContain("ToolTip=\"Настройки лаунчера\"", classic.ToString());
        Assert.DoesNotContain("ToolTip=\"Настройки лаунчера\"", pda.ToString());
    }

    [Fact]
    public void PdaShowsErrorsInsideBothThemes()
    {
        var dialogService = new DialogService();
        string? displayedTitle = null;
        string? displayedMessage = null;
        dialogService.ErrorRequested += (title, message) =>
        {
            displayedTitle = title;
            displayedMessage = message;
        };

        dialogService.ShowError("Ошибка запуска", "Файл не найден.");

        Assert.Equal("Ошибка запуска", displayedTitle);
        Assert.Equal("Файл не найден.", displayedMessage);

        var pda = LoadProjectXaml("Views", "Controls", "PdaMainView.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = Assert.Single(pda.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "InlineErrorPanel");
        Assert.Equal("{DynamicResource ModArchiveProgressPanelStyle}", (string?)panel.Attribute("Style"));
        Assert.Contains("DismissErrorButton_OnClick", panel.ToString());
    }

    [Fact]
    public void NewPdaUsesSeparateNeutralPalette()
    {
        var classicTheme = LoadProjectXaml("Themes", "PdaTheme.xaml");
        var newTheme = LoadProjectXaml("Themes", "NewPdaTheme.xaml");
        var pdaMain = LoadProjectXaml("Views", "Controls", "PdaMainView.xaml");
        var profileDrawer = LoadProjectXaml("Views", "Controls", "PdaProfileDrawerView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        static string BrushColor(XDocument document, XNamespace presentationNamespace, XNamespace xamlNamespace, string key) =>
            (string)document.Descendants(presentationNamespace + "SolidColorBrush")
                .Single(element => (string?)element.Attribute(xamlNamespace + "Key") == key)
                .Attribute("Color")!;

        Assert.Equal("#080C13", BrushColor(classicTheme, presentation, xaml, "WindowBackgroundBrush"));
        Assert.Equal("#17212D", BrushColor(classicTheme, presentation, xaml, "PdaMainTabBackgroundBrush"));
        Assert.Equal("#4A5866", BrushColor(classicTheme, presentation, xaml, "PdaCatalogButtonBorderBrush"));
        Assert.Equal("#0C0D0C", BrushColor(newTheme, presentation, xaml, "WindowBackgroundBrush"));
        Assert.Equal("#1A1C19", BrushColor(newTheme, presentation, xaml, "PdaMainTabBackgroundBrush"));
        Assert.Equal("#53574E", BrushColor(newTheme, presentation, xaml, "PdaCatalogButtonBorderBrush"));
        Assert.Contains("/CORDON;component/Themes/PdaTheme.xaml", newTheme.ToString());
        Assert.Contains("PdaThemeSelector.CurrentSource", pdaMain.ToString());
        Assert.Contains("PdaThemeSelector.CurrentDrawerAtlas", profileDrawer.ToString());
    }

    [Fact]
    public void ActivityLogsDoNotRepeatTheirPageTitles()
    {
        var classicLog = LoadProjectXaml("Views", "Controls", "ActivityLogView.xaml");
        var pdaLog = LoadProjectXaml("Views", "Controls", "PdaLogView.xaml");

        Assert.DoesNotContain("Text=\"Журнал\"", classicLog.ToString());
        Assert.DoesNotContain("ЖУРНАЛ ЛАУНЧЕРА", pdaLog.ToString());
    }

    [Fact]
    public void PdaCatalogModCardsHaveSquareCorners()
    {
        var catalog = LoadProjectXaml("Views", "Controls", "PdaModCatalogView.xaml");
        var card = catalog.Descendants()
            .Single(element =>
                element.Name.LocalName == "Border" &&
                (string?)element.Attribute("Padding") == "8" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name" && attribute.Value == "Chrome"));

        Assert.Equal("0", (string?)card.Attribute("CornerRadius"));
    }

    [Fact]
    public void AboutPagesDoNotExposeUpdateOrInterfaceSwitchButtons()
    {
        var classicAbout = LoadProjectXaml("Views", "AboutWindow.xaml");
        var pdaAbout = LoadProjectXaml("Views", "Controls", "PdaAboutView.xaml");

        Assert.Contains("Text=\"CORDON\"", classicAbout.ToString());
        Assert.Contains("S.T.A.L.K.E.R. Mod Launcher", classicAbout.ToString());
        Assert.Contains("Text=\"CORDON\"", pdaAbout.ToString());
        Assert.Contains("S.T.A.L.K.E.R. Mod Launcher", pdaAbout.ToString());
        Assert.DoesNotContain("Проверить обновления", classicAbout.ToString());
        Assert.DoesNotContain("Интерфейс КПК", classicAbout.ToString());
        Assert.DoesNotContain("Проверить обновления", pdaAbout.ToString());
    }

    [Fact]
    public void LauncherBrandingUsesLargerClassicTextAndSingleLinePdaText()
    {
        var classicBrand = LoadProjectXaml("Views", "Controls", "LauncherBrand.xaml");
        var pdaMain = LoadProjectXaml("Views", "Controls", "PdaMainView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var classicTitle = Assert.Single(classicBrand.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "CORDON");
        var classicSubtitle = Assert.Single(classicBrand.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "S.T.A.L.K.E.R. Mod Launcher");
        Assert.Equal("28", (string?)classicTitle.Attribute("FontSize"));
        Assert.Equal("12.5", (string?)classicSubtitle.Attribute("FontSize"));

        var pdaTitle = Assert.Single(pdaMain.Descendants(presentation + "TextBlock"), element =>
            element.Descendants(presentation + "Run").Any(run => (string?)run.Attribute("Text") == "CORDON"));
        Assert.Equal("NoWrap", (string?)pdaTitle.Attribute("TextWrapping"));
        Assert.Contains("S.T.A.L.K.E.R. Mod Launcher", pdaTitle.ToString());
        Assert.DoesNotContain(" — ", pdaTitle.ToString());
        Assert.Contains(pdaTitle.Descendants(presentation + "Run"), run =>
            (string?)run.Attribute("Text") == "\u00A0\u00A0");
    }

    private static Grid CreateThemeHost()
    {
        var host = new Grid { Width = 600, Height = 480 };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CORDON;component/Themes/Palette.xaml", UriKind.RelativeOrAbsolute)
        });
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CORDON;component/Themes/SharedStyles.xaml", UriKind.RelativeOrAbsolute)
        });
        return host;
    }

    private static Grid CreatePdaThemeHost(bool useNewTheme = false)
    {
        var host = new Grid { Width = 908, Height = 521 };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                useNewTheme
                    ? "/CORDON;component/Themes/NewPdaTheme.xaml"
                    : "/CORDON;component/Themes/PdaTheme.xaml",
                UriKind.RelativeOrAbsolute)
        });
        return host;
    }

    private static XDocument LoadProjectXaml(params string[] parts)
    {
        return XDocument.Load(FindProjectFile(parts));
    }

    private static string LoadProjectText(params string[] parts)
    {
        return File.ReadAllText(FindProjectFile(parts));
    }

    private static string FindProjectFile(string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var projectRoot = Path.Combine(directory.FullName, "src", "StalkerModLauncher");
            if (File.Exists(Path.Combine(projectRoot, "StalkerModLauncher.csproj")))
            {
                return Path.Combine([projectRoot, .. parts]);
            }
        }

        throw new DirectoryNotFoundException("StalkerModLauncher project root was not found.");
    }
}
