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
    private int _loadVersion;

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
        var loadVersion = ++_loadVersion;
        StatusText.Text = Strings.Saves_Loading;
        StatusText.Visibility = Visibility.Visible;
        SavesList.Visibility = Visibility.Collapsed;
        GameMenuActions.Visibility = Visibility.Collapsed;
        try
        {
            var profile = CaptureCatalogProfile(_profile);
            var canLaunch = _canLaunch;
            var (hasDirectLaunch, saves) = await Task.Run(() =>
            {
                var catalog = SavedGameCatalog.Preview(profile);
                var hasDirectLaunch = catalog.Extension is not null;
                var rows = catalog.Enumerate()
                    .Select(file => new SaveRow(Path.GetFileNameWithoutExtension(file.Name), file.FullName,
                        ReadThumbnail(file.FullName), canLaunch && hasDirectLaunch))
                    .ToArray();
                return (hasDirectLaunch, rows);
            });
            if (!IsLoaded || loadVersion != _loadVersion) return;

            GameMenuActions.Visibility = hasDirectLaunch ? Visibility.Collapsed : Visibility.Visible;
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
            if (IsLoaded && loadVersion == _loadVersion)
                StatusText.Text = LocalizedText.Format(Strings.Saves_ReadFailedFormat, ex.Message);
        }
    }

    private static ModProfile CaptureCatalogProfile(ModProfile profile) => new()
    {
        Id = profile.Id,
        IsStandalone = profile.IsStandalone,
        UseBaseGameData = profile.UseBaseGameData,
        LaunchBackendKind = profile.LaunchBackendKind,
        GameInstallPath = profile.GameInstallPath,
        WorkspacePath = profile.WorkspacePath,
        Mo2OverwritePath = profile.Mo2OverwritePath,
        ExecutableRelativePath = profile.ExecutableRelativePath,
        ExecutableSourcePath = profile.ExecutableSourcePath,
        UsvfsExecutableOverrideRelativePath = profile.UsvfsExecutableOverrideRelativePath,
        FsgameSourcePath = profile.FsgameSourcePath,
        WorkingDirectoryRelative = profile.WorkingDirectoryRelative,
        LaunchArguments = profile.LaunchArguments,
        Mods = new(profile.Mods.Select(mod => new ModEntry
        {
            Id = mod.Id,
            Name = mod.Name,
            SourcePath = mod.SourcePath,
            IsEnabled = mod.IsEnabled,
            Order = mod.Order,
            ExcludedFiles = [.. mod.ExcludedFiles]
        }))
    };

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
