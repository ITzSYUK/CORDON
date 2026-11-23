using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Views;

public partial class SavesWindow : Window
{
    private readonly ModProfile _profile;

    public SavesWindow(ModProfile profile, bool canLaunch)
    {
        _profile = profile;
        var requiresSwmLauncher = SavedGameLaunch.IsSwmLauncher(profile.ExecutableRelativePath);
        CanLaunch = canLaunch && !requiresSwmLauncher;
        InitializeComponent();
        Title = LocalizedText.Format(Strings.Saves_TitleFormat, profile.Name);
        if (requiresSwmLauncher)
        {
            SwmActions.Visibility = Visibility.Visible;
            OpenSwmButton.IsEnabled = canLaunch;
        }
    }

    public bool CanLaunch { get; }
    public string? SelectedSavePath { get; private set; }

    private void Window_OnSourceInitialized(object? sender, EventArgs e) =>
        WindowSystemIntegrationService.Initialize(this);

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var directories = ProfileDataPathResolver.GetSavedGameDirectories(_profile);
            var saves = await Task.Run(() => SavedGameLaunch.Enumerate(directories)
                .Select(file => new SaveRow(
                    Path.GetFileNameWithoutExtension(file.Name), file.FullName, ReadThumbnail(file.FullName)))
                .ToArray());
            if (!IsLoaded) return;

            if (saves.Length == 0)
            {
                StatusText.Text = Strings.Saves_Empty;
                return;
            }

            SavesList.ItemsSource = saves;
            SavesList.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
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
        if (CanLaunch && sender is Button { DataContext: SaveRow save })
        {
            SelectedSavePath = save.FilePath;
            DialogResult = true;
        }
    }

    private void OpenSwmButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }

    private sealed record SaveRow(string Name, string FilePath, BitmapSource? Thumbnail);
}
