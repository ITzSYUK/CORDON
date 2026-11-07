using System.Windows;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views.Controls;

namespace StalkerModLauncher.Views;

public partial class ProfileImportWindow : Window
{
    public ProfileImportWindow(ProfileImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var view = new ProfileImportContentView();
        Content = view;
        view.Cancelled += (_, _) => DialogResult = false;
        viewModel.Completed += (_, _) => DialogResult = true;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e) =>
        WindowSystemIntegrationService.Initialize(this);
}
