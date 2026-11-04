using System.ComponentModel;
using System.Windows;
using StalkerModLauncher.Services;
using StalkerModLauncher.Views.Controls;

namespace StalkerModLauncher.Views;

public partial class StorageMigrationWindow : Window
{
    internal StorageMigrationView ContentView { get; }

    public StorageMigrationWindow(string destination, string sources, Func<IProgress<(double Fraction, string Path)>, Task>? transfer = null, DialogService? dialogs = null)
        : this(new StorageMigrationView(destination, sources, transfer, dialogs)) { }

    public StorageMigrationWindow(StorageMigrationView view)
    {
        InitializeComponent();
        ContentView = view;
        MigrationHost.Content = view;
        view.Completed += (_, accepted) => DialogResult = accepted;
        Closing += Window_OnClosing;
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e) => e.Cancel = ContentView.IsTransferring;
    private void Window_OnSourceInitialized(object? sender, EventArgs e) => WindowSystemIntegrationService.Initialize(this);
}
