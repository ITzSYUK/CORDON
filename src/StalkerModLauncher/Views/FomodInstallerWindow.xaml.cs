using System.Windows;
using StalkerModLauncher.Services;
using StalkerModLauncher.Services.Fomod;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views;

public sealed partial class FomodInstallerWindow : Window
{
    private readonly FomodInstallerViewModel _viewModel;

    public FomodInstallerWindow(FomodModule module, string packageRoot)
        : this(new FomodInstallerViewModel(module, packageRoot))
    {
    }

    public FomodInstallerWindow(FomodInstallerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Completed += ViewModel_Completed;
        Closed += (_, _) =>
        {
            viewModel.Completed -= ViewModel_Completed;
            viewModel.Dispose();
        };
    }

    public FomodSelector.Selection? GetSelection() =>
        _viewModel.Completion.IsCompletedSuccessfully ? _viewModel.Completion.Result : null;

    private void ViewModel_Completed(object? sender, EventArgs e) => Close();

    private void FomodInstallerWindow_OnSourceInitialized(object? sender, EventArgs e) =>
        WindowSystemIntegrationService.Initialize(this);
}
