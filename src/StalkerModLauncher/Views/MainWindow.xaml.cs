using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StalkerModLauncher.Services;
using StalkerModLauncher.Models;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views;

public partial class MainWindow : Window
{
    private bool _isClosingAfterCleanup;
    private bool _isClosing;
    private bool _initialInterfaceReady;
    private bool _isSwitchingToClassic;
    private bool _isSwitchingPdaTheme;
    private PdaWindow? _pdaWindow;
    private readonly WindowNavigationService _navigation;

    public MainWindow(
        MainViewModel viewModel,
        WindowNavigationService navigation)
    {
        InitializeComponent();
        _navigation = navigation;
        viewModel.ActivityLog.PropertyChanged += ActivityLog_PropertyChanged;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.ProfileCreationRequested += ViewModel_ProfileCreationRequested;
        viewModel.Mo2ImportRequested += ViewModel_Mo2ImportRequested;
        viewModel.ConflictExplorerRequested += ViewModel_ConflictExplorerRequested;
        DataContext = viewModel;
    }

    private void ViewModel_ProfileCreationRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null || _pdaWindow is not null)
        {
            return;
        }

        WindowNavigationService.ShowProfileCreation(this, ViewModel);
    }

    private void ViewModel_ConflictExplorerRequested(object? sender, ModEntry? mod)
    {
        if (ViewModel is not null && _pdaWindow is null)
        {
            WindowNavigationService.ShowConflictExplorer(this, ViewModel.CreateConflictExplorerViewModel(mod));
        }
    }

    private void ViewModel_Mo2ImportRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not null && _pdaWindow is null)
        {
            _navigation.ShowMo2Import(this, ViewModel);
        }
    }

    private void ActivityLog_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityLogViewModel.IsVisible))
        {
            var row = RootGrid.RowDefinitions[2];
            var vm = (MainViewModel)DataContext;
            BindingOperations.SetBinding(row, RowDefinition.HeightProperty,
                new Binding(nameof(ActivityLogViewModel.RowHeight)) { Source = vm.ActivityLog });
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowSystemIntegrationService.Initialize(this);
    }

    public async Task<bool> ShowInitialInterfaceAsync(bool startHidden = false)
    {
        if (ViewModel is null)
        {
            if (!startHidden)
            {
                Show();
            }

            return false;
        }

        await ViewModel.Initialization;
        _initialInterfaceReady = true;
        if (ViewModel.IsPdaInterfaceEnabled)
        {
            if (!startHidden)
            {
                ShowPdaWindow();
            }

            return true;
        }

        if (!startHidden)
        {
            Show();
        }

        return false;
    }

    private void EditProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settingsVm = ViewModel?.CreateProfileSettingsViewModel();
        if (settingsVm is null)
        {
            return;
        }

        WindowNavigationService.ShowProfileSettings(this, settingsVm);
    }

    private void ScreenshotsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = ViewModel?.SelectedProfile;
        if (profile is null)
        {
            return;
        }

        _navigation.ShowScreenshots(this, profile);
    }

    private void ModCatalogButton_OnClick(object sender, RoutedEventArgs e)
    {
        _navigation.ShowModCatalog(this);
    }

    private void LauncherSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            new LauncherSettingsWindow(ViewModel.CreateLauncherSettingsViewModel(
                () => _navigation.CheckForUpdatesAsync()))
            {
                Owner = this
            }.ShowDialog();
        }
    }

    private void ProfileHealthButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = ViewModel?.SelectedProfile;
        if (profile is null)
        {
            return;
        }

        _navigation.ShowProfileHealth(this, profile, ViewModel is null ? null : ViewModel.AppendLog);
    }

    private async void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        await _navigation.ShowAboutAsync(this);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initialInterfaceReady ||
            e.PropertyName is not nameof(MainViewModel.IsPdaInterfaceEnabled) and not nameof(MainViewModel.UseNewPdaInterface) ||
            ViewModel is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (e.PropertyName == nameof(MainViewModel.UseNewPdaInterface) &&
                _pdaWindow is not null &&
                _pdaWindow.UsesNewTheme != ViewModel.UseNewPdaInterface)
            {
                _isSwitchingPdaTheme = true;
                _pdaWindow.Close();
                return;
            }

            if (ViewModel.IsPdaInterfaceEnabled)
            {
                ShowPdaWindow();
            }
            else
            {
                if (_pdaWindow is not null)
                {
                    _isSwitchingToClassic = true;
                    _pdaWindow.Close();
                }
                else if (!IsVisible)
                {
                    Show();
                    Activate();
                }
            }
        });
    }

    private void ShowPdaWindow()
    {
        if (ViewModel is null || _pdaWindow is not null)
        {
            return;
        }

        _pdaWindow = new PdaWindow(ViewModel, _navigation);
        _pdaWindow.Closed += (_, _) =>
        {
            _pdaWindow = null;
            if (_isClosingAfterCleanup)
            {
                return;
            }

            if (_isSwitchingToClassic)
            {
                _isSwitchingToClassic = false;
                Show();
                Activate();
                return;
            }

            if (_isSwitchingPdaTheme)
            {
                _isSwitchingPdaTheme = false;
                ShowPdaWindow();
                return;
            }

            Close();
        };
        _pdaWindow.Show();
        Hide();
    }

    public void ShowFromTray()
    {
        if (ViewModel?.IsPdaInterfaceEnabled == true)
        {
            if (_pdaWindow is null)
            {
                ShowPdaWindow();
            }
            else
            {
                _pdaWindow.Show();
                _pdaWindow.Activate();
            }

            Hide();
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void HideToTray()
    {
        _pdaWindow?.Hide();
        Hide();
    }

    public void CloseAfterCleanup()
    {
        _isClosingAfterCleanup = true;
        _pdaWindow?.Close();
        Close();
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterCleanup)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        if (ViewModel?.MinimizeToTrayOnClose == true)
        {
            HideToTray();
            return;
        }

        _isClosing = true;
        if (ViewModel is not null)
        {
            await ViewModel.CleanupAsync();
        }

        _isClosingAfterCleanup = true;
        _pdaWindow?.Close();
        Close();
        Application.Current.Shutdown();
    }

}
