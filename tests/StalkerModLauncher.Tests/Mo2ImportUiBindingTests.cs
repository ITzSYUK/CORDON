using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Services;
using StalkerModLauncher.Views.Controls;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class Mo2ImportUiBindingTests
{
    [Theory]
    [InlineData("Views/Mo2ImportWindow.xaml")]
    [InlineData("Views/Controls/PdaMo2ImportView.xaml")]
    public void EnabledStateBindingIsOneWayForReadOnlyPreviewEntry(string relativePath)
    {
        var projectRoot = FindProjectRoot();
        var document = ReadSharedContent(projectRoot, relativePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var enabledColumn = document
            .Descendants(presentation + "DataGridCheckBoxColumn")
            .Single(column => ((string?)column.Attribute("Binding"))?.Contains("IsEnabled", StringComparison.Ordinal) == true);

        Assert.Contains("Mode=OneWay", (string?)enabledColumn.Attribute("Binding"));
    }

    [Theory]
    [InlineData("Views/Mo2ImportWindow.xaml")]
    [InlineData("Views/Controls/PdaMo2ImportView.xaml")]
    public void PreviewExposesProblemFilterAndNoBackendChoice(string relativePath)
    {
        var projectRoot = FindProjectRoot();
        var document = ReadSharedContent(projectRoot, relativePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "DataGrid"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains("VisibleEntries", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => ((string?)element.Attribute("Command"))?.Contains("BrowseMissingModFolderCommand", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            document.Descendants(presentation + "RadioButton"),
            element => (string?)element.Attribute("Content") == "Workspace");
        Assert.DoesNotContain(
            document.Descendants(presentation + "RadioButton"),
            element => (string?)element.Attribute("Content") == "USVFS");
    }

    [Fact]
    public void AmbiguousEntryHasSharedRowHighlight()
    {
        var projectRoot = FindProjectRoot();
        var stylesPath = Path.Combine(projectRoot, "Themes", "Mo2ImportStyles.xaml");
        var document = XDocument.Load(stylesPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var rowStyle = document
            .Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(xaml + "Key") == "Mo2DataGridRowStyle");
        var ambiguityTrigger = rowStyle
            .Descendants(presentation + "DataTrigger")
            .Single(trigger => ((string?)trigger.Attribute("Binding"))?.Contains("IsAmbiguous", StringComparison.Ordinal) == true);

        Assert.Equal("True", (string?)ambiguityTrigger.Attribute("Value"));
        Assert.Contains(
            ambiguityTrigger.Descendants(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Background");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPreviewKeepsRowFolderCommandAndReadOnlyEnabledState(bool usePdaTheme)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var entry = new Mo2ImportPreviewEntry(1, "Missing", string.Empty, true, string.Empty, false, []);
                object? selectedEntry = null;
                var browse = new RelayCommand(parameter => selectedEntry = parameter);
                var view = new Mo2ImportContentView
                {
                    UsePdaTheme = usePdaTheme,
                    DataContext = new PreviewBindings(entry, browse)
                };
                // This binding test has no Application; decorative checkbox images are covered by the app smoke test.
                view.Resources["Mo2ContentCheckBoxStyle"] = new Style(typeof(CheckBox));
                var enabledColumn = (DataGridCheckBoxColumn)view.FindName("EnabledColumn");
                enabledColumn.ElementStyle = new Style(typeof(CheckBox));
                view.Measure(new Size(900, 550));
                view.Arrange(new Rect(0, 0, 900, 550));
                view.UpdateLayout();

                var grid = Assert.Single(VisualChildren<DataGrid>(view));
                Assert.True(grid.IsReadOnly);
                Assert.Same(entry, Assert.Single(grid.Items.Cast<Mo2ImportPreviewEntry>()));
                var button = Assert.Single(VisualChildren<Button>(grid), item => ReferenceEquals(item.Command, browse));
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.Same(entry, button.CommandParameter);
                button.Command!.Execute(button.CommandParameter);
                Assert.Same(entry, selectedEntry);
                Assert.True(entry.IsEnabled);
                Assert.IsType<TextBox>(view.InitialFocusTarget);
                view.UsePdaTheme = !usePdaTheme;
                Assert.Same(entry, Assert.Single(grid.Items.Cast<Mo2ImportPreviewEntry>()));
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

    private sealed class PreviewBindings(Mo2ImportPreviewEntry entry, RelayCommand browse)
    {
        public bool IsSourceStep { get; }
        public bool IsPreviewStep { get; } = true;
        public IReadOnlyList<Mo2ImportPreviewEntry> VisibleEntries { get; } = [entry];
        public string SearchText { get; set; } = string.Empty;
        public RelayCommand BrowseMissingModFolderCommand { get; } = browse;
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static XDocument ReadSharedContent(string projectRoot, string relativePath)
    {
        var shell = XDocument.Load(Path.Combine(projectRoot, relativePath));
        XNamespace controls = "clr-namespace:StalkerModLauncher.Views.Controls";
        Assert.Single(shell.Descendants(controls + "Mo2ImportContentView"));
        return XDocument.Load(Path.Combine(projectRoot, "Views", "Controls", "Mo2ImportContentView.xaml"));
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
}
