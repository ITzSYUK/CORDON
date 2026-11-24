using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Views.Controls;

public partial class SavesView : UserControl
{
    private readonly ModProfile _profile;
    private readonly bool _canLaunch;

    public SavesView(ModProfile profile, bool canLaunch, bool usePdaTheme = false)
    {
        _profile = profile;
        _canLaunch = canLaunch;
        UsePdaTheme = usePdaTheme;
        InitializeComponent();
        if (usePdaTheme)
        {
            var theme = new ResourceDictionary { Source = Themes.PdaThemeSelector.CurrentSource };
            Resources.MergedDictionaries.Add(theme);
            SetResourceReference(BackgroundProperty, "PdaPageBackgroundBrush");
        }
    }

    public bool UsePdaTheme { get; }
    public event EventHandler<string?>? LaunchRequested;

    private async void SavesView_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var catalog = SavedGameCatalog.Preview(_profile);
            var canLaunchSave = _canLaunch && catalog.Extension is not null;
            var saves = await Task.Run(() => catalog.Enumerate()
                .Select(file => new SaveRow(
                    Path.GetFileNameWithoutExtension(file.Name), file.FullName, ReadThumbnail(file.FullName), canLaunchSave))
                .ToArray());
            if (!IsLoaded) return;

            GameMenuActions.Visibility = catalog.Extension is null ? Visibility.Visible : Visibility.Collapsed;
            OpenGameButton.IsEnabled = _canLaunch;
            if (saves.Length == 0)
            {
                StatusText.Text = Strings.Saves_Empty;
                return;
            }

            SavesList.ItemsSource = saves;
            SavesList.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            StatusText.Text = LocalizedText.Format(Strings.Saves_ReadFailedFormat, ex.Message);
        }
    }

    private static BitmapSource? ReadThumbnail(string savePath)
    {
        var imagePath = Path.ChangeExtension(savePath, ".dds");
        if (!File.Exists(imagePath)) return null;

        try
        {
            return ScreenshotImageLoader.LoadSaveThumbnail(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void LaunchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SaveRow { CanLaunch: true } save })
            LaunchRequested?.Invoke(this, save.FilePath);
    }

    private void OpenGameButton_OnClick(object sender, RoutedEventArgs e) => LaunchRequested?.Invoke(this, null);

    private sealed record SaveRow(string Name, string FilePath, BitmapSource? Thumbnail, bool CanLaunch);
}
