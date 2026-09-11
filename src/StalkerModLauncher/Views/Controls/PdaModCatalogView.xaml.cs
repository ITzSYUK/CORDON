using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class PdaModCatalogView : UserControl
{
    private bool _loaded;

    public PdaModCatalogView()
    {
        InitializeComponent();
    }

    private ModCatalogViewModel? ViewModel => DataContext as ModCatalogViewModel;

    private async void PdaModCatalogView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || ViewModel is null) return;
        _loaded = true;
        UpdateCategoryButtons(ApProCatalogCategory.ShadowOfChernobyl);
        await ViewModel.LoadInitialAsync(ApProCatalogCategory.ShadowOfChernobyl);
    }

    private async void ShadowOfChernobylButton_OnClick(object sender, RoutedEventArgs e) => await LoadCategoryAsync(ApProCatalogCategory.ShadowOfChernobyl);
    private async void ClearSkyButton_OnClick(object sender, RoutedEventArgs e) => await LoadCategoryAsync(ApProCatalogCategory.ClearSky);
    private async void CallOfPripyatButton_OnClick(object sender, RoutedEventArgs e) => await LoadCategoryAsync(ApProCatalogCategory.CallOfPripyat);

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        CatalogScrollViewer.ScrollToTop();
        await ViewModel.LoadInitialAsync(ViewModel.SelectedCategory, true);
    }

    private void ListingButton_OnClick(object sender, RoutedEventArgs e) => ModCatalogViewModel.OpenListing((sender as FrameworkElement)?.DataContext as ModCatalogItemViewModel);

    private void ApProLink_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ModCatalogViewModel.OpenWebsite();

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e) => CatalogScrollViewer.ScrollToTop();

    private void SearchBoxChrome_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchTextBox.IsKeyboardFocusWithin)
        {
            SearchTextBox.Focus();
            e.Handled = true;
        }
    }

    private async void CatalogScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 360)
        {
            await (ViewModel?.LoadNextPageAsync() ?? Task.CompletedTask);
        }
    }

    private async Task LoadCategoryAsync(ApProCatalogCategory category)
    {
        if (ViewModel is null || ViewModel.SelectedCategory == category) return;
        UpdateCategoryButtons(category);
        CatalogScrollViewer.ScrollToTop();
        await ViewModel.LoadInitialAsync(category);
    }

    private void UpdateCategoryButtons(ApProCatalogCategory category)
    {
        SetButtonState(ShadowOfChernobylButton, category == ApProCatalogCategory.ShadowOfChernobyl);
        SetButtonState(ClearSkyButton, category == ApProCatalogCategory.ClearSky);
        SetButtonState(CallOfPripyatButton, category == ApProCatalogCategory.CallOfPripyat);
    }

    private static void SetButtonState(Button button, bool active)
    {
        button.SetResourceReference(Control.BackgroundProperty, active ? "AccentBrush" : "PdaControlBackgroundBrush");
        button.SetResourceReference(Control.ForegroundProperty, active ? "PdaCatalogActiveTextBrush" : "PdaCatalogButtonTextBrush");
        button.SetResourceReference(Control.BorderBrushProperty, active ? "PdaCatalogActiveBorderBrush" : "PdaCatalogButtonBorderBrush");
    }
}
