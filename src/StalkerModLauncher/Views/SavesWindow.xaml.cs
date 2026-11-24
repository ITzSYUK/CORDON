using System.Windows;
using System.Windows.Input;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.Views.Controls;

namespace StalkerModLauncher.Views;

public partial class SavesWindow : Window
{
    public SavesWindow(ModProfile profile, bool canLaunch)
    {
        InitializeComponent();
        Title = Strings.Saves_Title;
        var view = new SavesView(profile, canLaunch);
        view.LaunchRequested += (_, path) =>
        {
            SelectedSavePath = path;
            DialogResult = true;
        };
        SavesHost.Content = view;
    }

    public string? SelectedSavePath { get; private set; }

    private void Window_OnSourceInitialized(object? sender, EventArgs e) =>
        WindowSystemIntegrationService.Initialize(this);

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }
}
