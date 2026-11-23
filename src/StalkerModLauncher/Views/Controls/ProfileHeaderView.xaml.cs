using System.Windows;
using System.Windows.Controls;

namespace StalkerModLauncher.Views.Controls;

public partial class ProfileHeaderView : UserControl
{
    public ProfileHeaderView()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? ProfileHealthRequested;
    public event RoutedEventHandler? ProfileSettingsRequested;
    public event RoutedEventHandler? ScreenshotsRequested;
    public event RoutedEventHandler? SavesRequested;

    private void ProfileHealthButton_OnClick(object sender, RoutedEventArgs e)
    {
        ProfileHealthRequested?.Invoke(this, e);
    }

    private void EditProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ProfileSettingsRequested?.Invoke(this, e);
    }

    private void ScreenshotsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ScreenshotsRequested?.Invoke(this, e);
    }

    private void SavesButton_OnClick(object sender, RoutedEventArgs e)
    {
        SavesRequested?.Invoke(this, e);
    }
}
