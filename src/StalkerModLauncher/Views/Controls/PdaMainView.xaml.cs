using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StalkerModLauncher.Views.Controls;

public partial class PdaMainView : UserControl
{
    private readonly DispatcherTimer _clockTimer;
    private IDisposable? _pageLifetime;
    private bool _isDrawerOpen;

    public PdaMainView()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    public event RoutedEventHandler? ProfileHealthRequested;
    public event RoutedEventHandler? ProfileSettingsRequested;
    public event RoutedEventHandler? ScreenshotsRequested;
    public event RoutedEventHandler? ModCatalogRequested;
    public event RoutedEventHandler? AboutRequested;
    public event RoutedEventHandler? LogRequested;
    public event RoutedEventHandler? LauncherSettingsRequested;

    public void ShowError(string title, string message)
    {
        InlineErrorTitle.Text = title;
        InlineErrorMessage.Text = message;
        InlineErrorPanel.Visibility = Visibility.Visible;
    }

    public void ShowPage(
        FrameworkElement page,
        string title,
        string status = "",
        IDisposable? lifetime = null,
        bool showProfileTypeIcon = false)
    {
        CloseDrawer();
        DisposeCurrentPage();
        _pageLifetime = lifetime;
        PageHost.Content = page;
        PageHost.Visibility = Visibility.Visible;
        ProfilePage.Visibility = Visibility.Collapsed;
        EmptyProfilePage.Visibility = Visibility.Collapsed;
        BindingOperations.ClearBinding(PageTitleText, TextBlock.TextProperty);
        BindingOperations.ClearBinding(PageStatusText, TextBlock.TextProperty);
        PageTitleText.Text = title;
        PageStatusText.Text = status;
        ProfileTypeIcon.Visibility = showProfileTypeIcon ? Visibility.Visible : Visibility.Collapsed;
        PageSectionIcon.Visibility = showProfileTypeIcon ? Visibility.Collapsed : Visibility.Visible;
        SetActiveNavigationButton(page switch
        {
            PdaScreenshotsView => ScreenshotsNavButton,
            PdaHealthView => StatusNavButton,
            PdaProfileSettingsView => SettingsNavButton,
            _ => null
        });
    }

    public void ShowProfilePage()
    {
        CloseDrawer();
        DisposeCurrentPage();
        PageHost.Content = null;
        PageHost.Visibility = Visibility.Collapsed;
        ProfilePage.ClearValue(VisibilityProperty);
        EmptyProfilePage.ClearValue(VisibilityProperty);
        PageTitleText.SetBinding(TextBlock.TextProperty, new Binding("SelectedProfile.Name") { FallbackValue = "Профиль не выбран" });
        PageStatusText.SetBinding(TextBlock.TextProperty, new Binding("ValidationSummary"));
        ProfileTypeIcon.Visibility = Visibility.Visible;
        PageSectionIcon.Visibility = Visibility.Collapsed;
        SetActiveNavigationButton(null);
    }

    private void SetActiveNavigationButton(Button? activeButton)
    {
        ScreenshotsNavButton.Tag = ReferenceEquals(activeButton, ScreenshotsNavButton) ? "Active" : null;
        StatusNavButton.Tag = ReferenceEquals(activeButton, StatusNavButton) ? "Active" : null;
        SettingsNavButton.Tag = ReferenceEquals(activeButton, SettingsNavButton) ? "Active" : null;
    }

    private void PdaMainView_OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _clockTimer.Start();
    }

    private void PdaMainView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clockTimer.Stop();
        DisposeCurrentPage();
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);

    private void ProfileTab_OnClick(object sender, RoutedEventArgs e) => ShowProfilePage();
    private void CatalogTab_OnClick(object sender, RoutedEventArgs e) => ModCatalogRequested?.Invoke(this, e);
    private void SettingsTab_OnClick(object sender, RoutedEventArgs e) => ProfileSettingsRequested?.Invoke(this, e);
    private void StatusButton_OnClick(object sender, RoutedEventArgs e) => ProfileHealthRequested?.Invoke(this, e);
    private void ScreenshotsButton_OnClick(object sender, RoutedEventArgs e) => ScreenshotsRequested?.Invoke(this, e);
    private void AboutButton_OnClick(object sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, e);
    private void LogButton_OnClick(object sender, RoutedEventArgs e) => LogRequested?.Invoke(this, e);
    private void LauncherSettingsButton_OnClick(object sender, RoutedEventArgs e) => LauncherSettingsRequested?.Invoke(this, e);
    private void PowerButton_OnClick(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    private void DismissErrorButton_OnClick(object sender, RoutedEventArgs e) => InlineErrorPanel.Visibility = Visibility.Collapsed;

    private void ProfilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isDrawerOpen) CloseDrawer(); else OpenDrawer();
    }

    private void ProfileDrawer_OnProfileSelected(object? sender, EventArgs e)
    {
        ShowProfilePage();
    }

    private void DrawerDim_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => CloseDrawer();

    private void OpenDrawer()
    {
        _isDrawerOpen = true;
        DrawerDim.Visibility = Visibility.Visible;
        ProfileDrawer.Visibility = Visibility.Visible;
        DrawerTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(-325, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        ProfileDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        DrawerDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    private void CloseDrawer()
    {
        if (!_isDrawerOpen) return;

        _isDrawerOpen = false;
        var slide = new DoubleAnimation(0, -325, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        slide.Completed += (_, _) =>
        {
            ProfileDrawer.Visibility = Visibility.Collapsed;
            DrawerDim.Visibility = Visibility.Collapsed;
        };
        DrawerTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        ProfileDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)));
        DrawerDim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)));
    }

    private void DisposeCurrentPage()
    {
        _pageLifetime?.Dispose();
        _pageLifetime = null;
    }
}
