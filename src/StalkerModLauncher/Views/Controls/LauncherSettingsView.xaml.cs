using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class LauncherSettingsView : UserControl
{
    private LauncherSettingsViewModel? _updateViewModel;

    public LauncherSettingsView()
    {
        InitializeComponent();
        DataContextChanged += LauncherSettingsView_OnDataContextChanged;
    }

    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LauncherSettingsViewModel viewModel && await viewModel.TrySaveAsync())
        {
            if (viewModel.IsLanguageChangePending)
            {
                DialogService.ShowInfo(Strings.Settings_LanguageRestartTitle, Strings.Settings_LanguageRestartMessage);
            }

            Saved?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) =>
        Cancelled?.Invoke(this, EventArgs.Empty);

    private void LauncherSettingsView_OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_updateViewModel is not null)
        {
            _updateViewModel.PropertyChanged -= LauncherSettingsViewModel_OnPropertyChanged;
        }

        _updateViewModel = e.NewValue as LauncherSettingsViewModel;
        if (_updateViewModel is not null)
        {
            _updateViewModel.PropertyChanged += LauncherSettingsViewModel_OnPropertyChanged;
        }
    }

    private void LauncherSettingsViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LauncherSettingsViewModel.HasAvailableUpdate))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_updateViewModel?.HasAvailableUpdate == true)
            {
                FadeIn(OpenReleaseButton);
                FadeIn(DownloadAndInstallButton);
            }
            else
            {
                ResetFade(OpenReleaseButton);
                ResetFade(DownloadAndInstallButton);
            }
        }, DispatcherPriority.DataBind);
    }

    private static void FadeIn(UIElement element) => element.BeginAnimation(
        OpacityProperty,
        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

    private static void ResetFade(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
    }
}
