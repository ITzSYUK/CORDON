using System.Windows;
using System.Windows.Input;
using StalkerModLauncher.Services;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views.Controls;

namespace StalkerModLauncher.Views;

public partial class PdaWindow : Window
{
    private readonly WindowNavigationService _navigation;

    public PdaWindow(MainViewModel viewModel, WindowNavigationService navigation)
    {
        UsesNewTheme = viewModel.UseNewPdaInterface;
        PdaThemeSelector.UseNewTheme = UsesNewTheme;
        InitializeComponent();
        _navigation = navigation;
        DataContext = viewModel;
        viewModel.ProfileCreationRequested += ViewModel_ProfileCreationRequested;
        viewModel.Mo2ImportRequested += ViewModel_Mo2ImportRequested;
        viewModel.ModScanSelectionRequested += ViewModel_ModScanSelectionRequested;
        viewModel.ConflictExplorerRequested += ViewModel_ConflictExplorerRequested;
        viewModel.DialogService.ErrorRequested += DialogService_ErrorRequested;
        Closed += (_, _) =>
        {
            viewModel.ProfileCreationRequested -= ViewModel_ProfileCreationRequested;
            viewModel.Mo2ImportRequested -= ViewModel_Mo2ImportRequested;
            viewModel.ModScanSelectionRequested -= ViewModel_ModScanSelectionRequested;
            viewModel.ConflictExplorerRequested -= ViewModel_ConflictExplorerRequested;
            viewModel.DialogService.ErrorRequested -= DialogService_ErrorRequested;
        };
    }

    public bool UsesNewTheme { get; }

    private void DialogService_ErrorRequested(string title, string message)
    {
        Dispatcher.Invoke(() => PdaView.ShowError(title, message));
    }

    private void ViewModel_ConflictExplorerRequested(object? sender, ModEntry? mod)
    {
        if (ViewModel is null)
        {
            return;
        }

        var viewModel = ViewModel.CreateConflictExplorerViewModel(mod);
        var page = new ConflictExplorerContentView
        {
            DataContext = viewModel,
            UsePdaTheme = true,
            CloseButtonText = Strings.Common_Back
        };
        page.CloseRequested += (_, _) => PdaView.ShowProfilePage();
        PdaView.ShowPage(
            page,
            Strings.Pda_ConflictTitle,
            ViewModel.SelectedProfile?.Name ?? string.Empty,
            viewModel,
            showProfileTypeIcon: true);
    }

    private void ViewModel_Mo2ImportRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var importViewModel = ViewModel.CreateMo2ImportViewModel();
        var page = new PdaMo2ImportView { DataContext = importViewModel };
        importViewModel.Completed += (_, _) => PdaView.ShowProfilePage();
        page.Cancelled += (_, _) => PdaView.ShowProfilePage();
        PdaView.ShowPage(page, Strings.Mo2_Title, "Mod Organizer 2");
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_ProfileCreationRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var creationViewModel = WindowNavigationService.CreateProfileCreationViewModel();
        var page = new PdaProfileCreationView { DataContext = creationViewModel };
        creationViewModel.Completed += (_, profile) =>
        {
            ViewModel.AddCreatedProfile(profile);
            PdaView.ShowProfilePage();
        };
        page.Cancelled += (_, _) => PdaView.ShowProfilePage();
        PdaView.ShowPage(page, Strings.Creation_Title);
    }

    private void EditProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settingsVm = ViewModel?.CreateProfileSettingsViewModel();
        if (settingsVm is not null)
        {
            var page = new PdaProfileSettingsView { DataContext = settingsVm };
            page.Saved += (_, _) => PdaView.ShowProfilePage();
            PdaView.ShowPage(page, LocalizedText.Format(Strings.Pda_ProfileSettingsFormat, settingsVm.ProfileName), showProfileTypeIcon: true);
        }
    }

    private void ViewModel_ModScanSelectionRequested(object? sender, ModScanSelectionEventArgs request)
    {
        var page = new PdaScanResultsView(request.Mods);
        page.Accepted += (_, _) =>
        {
            request.Accept(page.GetSelectedMods());
            PdaView.ShowProfilePage();
        };
        page.Cancelled += (_, _) =>
        {
            request.Cancel();
            PdaView.ShowProfilePage();
        };
        PdaView.ShowPage(page, Strings.Scan_Title, lifetime: page, showProfileTypeIcon: true);
    }

    private void ScreenshotsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProfile is { } profile)
        {
            var screenshotsVm = _navigation.CreateScreenshotsViewModel(profile);
            PdaView.ShowPage(
                new PdaScreenshotsView { DataContext = screenshotsVm },
                LocalizedText.Format(Strings.Pda_ScreenshotsFormat, profile.Name),
                lifetime: screenshotsVm,
                showProfileTypeIcon: true);
        }
    }

    private void ModCatalogButton_OnClick(object sender, RoutedEventArgs e)
    {
        var catalogVm = _navigation.CreateModCatalogViewModel();
        PdaView.ShowPage(
            new PdaModCatalogView { DataContext = catalogVm },
            Strings.Catalog_Title,
            "AP-PRO.RU",
            catalogVm);
    }

    private void ProfileHealthButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProfile is { } profile)
        {
            var healthVm = _navigation.CreateProfileHealthViewModel(profile, ViewModel.AppendLog);
            PdaView.ShowPage(
                new PdaHealthView { DataContext = healthVm },
                LocalizedText.Format(Strings.Pda_StatusFormat, profile.Name),
                healthVm.ProfileKind,
                healthVm,
                showProfileTypeIcon: true);
        }
    }

    private void AboutButton_OnClick(object sender, RoutedEventArgs e)
    {
        PdaView.ShowPage(new PdaAboutView(), Strings.Main_About);
    }

    private void LogButton_OnClick(object sender, RoutedEventArgs e)
    {
        PdaView.ShowPage(new PdaLogView { DataContext = ViewModel }, Strings.Log_Title);
    }

    private void LauncherSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var page = new LauncherSettingsView
        {
            DataContext = ViewModel.CreateLauncherSettingsViewModel(
                () => _navigation.CheckForUpdatesAsync())
        };
        page.Saved += (_, _) => PdaView.ShowProfilePage();
        page.Cancelled += (_, _) => PdaView.ShowProfilePage();
        PdaView.ShowPage(page, Strings.Settings_Title);
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.GetPosition(this).Y < 94)
        {
            DragMove();
        }
    }
}
