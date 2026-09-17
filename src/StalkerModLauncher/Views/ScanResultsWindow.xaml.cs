using System.Collections.ObjectModel;
using System.Windows;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views;

public sealed partial class ScanResultsWindow : Window
{
    public ObservableCollection<SelectableMod> Mods { get; } = new();

    public List<SelectableMod> GetSelectedMods()
    {
        return Mods.Where(m => m.IsSelected).ToList();
    }

    public ScanResultsWindow()
    {
        InitializeComponent();
        ModsListView.ItemsSource = Mods;
    }

    private void ScanResultsWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowSystemIntegrationService.Initialize(this);
    }

    private void ModsListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        foreach (SelectableMod mod in e.AddedItems)
        {
            mod.IsSelected = true;
        }

        foreach (SelectableMod mod in e.RemovedItems)
        {
            mod.IsSelected = false;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModsListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(Strings.Scan_NoneSelected, Strings.Scan_AddingTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

}
