using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using StalkerModLauncher.Views.Controls;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ConflictExplorerUiTests
{
    [Fact]
    public void ConflictExplorerRendersInClassicAndEmbeddedPdaThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Render(usePdaTheme: false);
                Render(usePdaTheme: true);
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
    public void ConflictExplorerUsesTabBarWithoutHeaderPanelOrCloseButton()
    {
        var projectRoot = FindProjectRoot();
        var document = XDocument.Load(Path.Combine(projectRoot, "Views", "Controls", "ConflictExplorerContentView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.DoesNotContain(
            document.Descendants(presentation + "TabControl"),
            element => ((string?)element.Attribute("SelectedIndex")) is not null);

        var tabRadios = document
            .Descendants(presentation + "RadioButton")
            .Where(element => (string?)element.Attribute("GroupName") == "ConflictTabs")
            .ToArray();
        Assert.Equal(2, tabRadios.Length);
        Assert.All(tabRadios, element => Assert.Contains("SelectTabCommand", (string?)element.Attribute("Command")));
        Assert.Equal(
            ["0", "1"],
            tabRadios.Select(element => (string?)element.Attribute("CommandParameter")).Order());

        var refreshButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => ((string?)element.Attribute("Command"))?.Contains("RefreshCommand", StringComparison.Ordinal) == true);
        Assert.Contains("ConflictTabButtonStyle", (string?)refreshButton.Attribute("Style"));

        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("Summary", StringComparison.Ordinal) == true);

        Assert.DoesNotContain(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Name.LocalName == "Click" && attribute.Value == "CloseButton_OnClick");
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "StalkerModLauncher");
            if (File.Exists(Path.Combine(candidate, "StalkerModLauncher.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("StalkerModLauncher project root was not found.");
    }

    private static void Render(bool usePdaTheme)
    {
        var view = new ConflictExplorerContentView
        {
            UsePdaTheme = usePdaTheme
        };
        view.Measure(new Size(980, 640));
        view.Arrange(new Rect(0, 0, 980, 640));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap(980, 640, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Assert.Equal(980, bitmap.PixelWidth);
        Assert.Equal(640, bitmap.PixelHeight);
    }
}
