using System.Windows;
using System.Windows.Controls;
using StalkerModLauncher.Resources;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public sealed partial class PdaScanResultsView : UserControl, IDisposable
{
    private bool _completed;

    public PdaScanResultsView(IReadOnlyList<SelectableMod> mods)
    {
        InitializeComponent();
        ModsListView.ItemsSource = mods;
        UpdateSelectionSummary();
    }

    public event EventHandler? Accepted;
    public event EventHandler? Cancelled;

    public IReadOnlyList<SelectableMod> GetSelectedMods() =>
        ModsListView.SelectedItems.Cast<SelectableMod>().ToList();

    public void Dispose()
    {
        if (!_completed)
        {
            _completed = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        GC.SuppressFinalize(this);
    }

    private void ModsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (SelectableMod mod in e.AddedItems)
        {
            mod.IsSelected = true;
        }

        foreach (SelectableMod mod in e.RemovedItems)
        {
            mod.IsSelected = false;
        }

        MessageText.Visibility = Visibility.Collapsed;
        UpdateSelectionSummary();
    }

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ModsListView.SelectedItems.Count == 0)
        {
            MessageText.Text = Strings.Scan_SelectOne;
            MessageText.Visibility = Visibility.Visible;
            return;
        }

        _completed = true;
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _completed = true;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectionSummary()
    {
        SelectionSummaryText.Text = LocalizedText.Format(Strings.Scan_SelectionFormat, ModsListView.Items.Count, ModsListView.SelectedItems.Count);
    }
}
