using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Models;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views.Controls;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileImportUiTests
{
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    [Theory]
    [InlineData(false, false, 900, 550)]
    [InlineData(true, false, 908, 521)]
    [InlineData(true, true, 959, 424)]
    public void PreviewRendersAndBindsEditableFieldsInAllInterfaces(bool pda, bool newTheme, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyPreview(pda, newTheme, width, height);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                PdaThemeSelector.UseNewTheme = false;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }
    internal static void VerifyPreview(bool pda, bool newTheme, int width, int height)
    {
        PdaThemeSelector.UseNewTheme = newTheme;
        var profile = new ModProfile { Name = "Imported", GameInstallPath = @"C:\Game" };
        profile.Mods.Add(new ModEntry { Name = "Textures", SourcePath = @"C:\Mods\Textures", Order = 1 });
        using var model = new ProfileImportViewModel(profile, _ => Task.FromResult(true));
        var view = new ProfileImportContentView(pda) { DataContext = model };
        var list = (ModImportListView)view.FindName("ModList");
        var grid = (DataGrid)list.FindName("ModsGrid");
        var host = new Border { Child = view };
        if (Application.Current is null)
        {
            // Image templates require a live Application, covered by the application smoke test.
            list.Resources["Mo2ContentCheckBoxStyle"] = new Style(typeof(CheckBox));
            ((DataGridCheckBoxColumn)grid.Columns[1]).ElementStyle = new Style(typeof(CheckBox));
        }
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var name = (TextBox)view.FindName("ProfileNameTextBox");
        var game = (TextBox)view.FindName("GamePathTextBox");
        Assert.Equal("Imported", name.Text);
        Assert.Equal(@"C:\Game", game.Text);
        Assert.Equal(name.TranslatePoint(new Point(), view).Y, game.TranslatePoint(new Point(), view).Y, 1);
        Assert.True(name.ActualWidth > 120);
        Assert.True(game.ActualWidth > 160);
        Assert.Equal(StalkerModLauncher.Resources.Strings.Mo2_ShowOnlyProblems, ((CheckBox)list.FindName("ProblemFilter")).Content);
        if (pda)
        {
            Assert.Equal(new Thickness(10, 5, 10, 10), ((Grid)view.FindName("RootGrid")).Margin);
            Assert.Equal(new Thickness(0, 0, 0, 5), ((Grid)view.FindName("ActionsPanel")).Margin);
        }
        name.Text = "Renamed";
        game.Text = @"D:\Game";
        Assert.Equal("Renamed", model.ProfileName);
        Assert.Equal(@"D:\Game", model.GamePath);
        Assert.Single(grid.Items);
        Assert.Equal(new Thickness(1), ((Border)view.FindName("PreviewBorder")).BorderThickness);
        Assert.Equal(6, grid.Columns.Count);
        var placeholder = VisualChildren<TextBlock>(list).Single(item => item.Text == StalkerModLauncher.Resources.Strings.Mo2_SearchPlaceholder);
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.False(Assert.Single(model.VisibleEntries).IsAvailable);
        model.SearchText = "Textures";
        host.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
        model.SearchText = string.Empty;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Assert.True(grid.ActualHeight > 100);
        Assert.True(grid.Columns[2].ActualWidth > 100);
        Assert.Same(model.ImportCommand, ((Button)view.FindName("ImportButton")).Command);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var previewDirectory = Environment.GetEnvironmentVariable("CORDON_TEST_PREVIEW_DIRECTORY");
        if (!string.IsNullOrEmpty(previewDirectory))
        {
            Directory.CreateDirectory(previewDirectory);
            using var output = File.Create(Path.Combine(previewDirectory, $"profile-import-{pda}-{newTheme}.png"));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(output);
        }
        Assert.Equal(width, bitmap.PixelWidth);
    }
}
