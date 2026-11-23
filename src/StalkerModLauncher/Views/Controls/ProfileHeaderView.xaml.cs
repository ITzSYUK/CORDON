using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

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

    private void ProfileActions_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var expanded = ProfileActionsToggle.IsChecked == true;
        ProfileActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ProfileActionsViewport.IsHitTestVisible = expanded;
        ProfileActionsViewport.IsEnabled = expanded;
        ProfileActionsViewport.BeginAnimation(WidthProperty, new DoubleAnimation(
            ProfileActionsViewport.Width, expanded ? ProfileActions.DesiredSize.Width : 0,
            new Duration(TimeSpan.FromSeconds(expanded ? 0.46 : 0.42)))
        {
            EasingFunction = new CubicEase { EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseInOut }
        });
        ProfileActionsViewport.BeginAnimation(OpacityProperty, new DoubleAnimation(
            ProfileActionsViewport.Opacity, expanded ? 1 : 0,
            new Duration(TimeSpan.FromSeconds(expanded ? 0.30 : 0.26)))
        {
            BeginTime = expanded ? TimeSpan.FromSeconds(0.08) : TimeSpan.Zero
        });
    }

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
