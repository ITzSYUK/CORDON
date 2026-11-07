using System.Windows;
using System.Windows.Controls;
using StalkerModLauncher.Themes;

namespace StalkerModLauncher.Views.Controls;

public partial class ProfileImportContentView : UserControl
{
    public ProfileImportContentView(bool usePdaTheme = false)
    {
        InitializeComponent();
        FontSize = usePdaTheme ? 11 : 12;
        if (usePdaTheme)
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = PdaThemeSelector.CurrentSource });
        ModList.UsePdaTheme = usePdaTheme;
        if (usePdaTheme)
        {
            RootGrid.Margin = new Thickness(10, 5, 10, 10);
            ActionsPanel.Margin = new Thickness(0, 0, 0, 5);
            PreviewBorder.SetResourceReference(BackgroundProperty, "PdaInsetPanelBrush");
        }
    }

    public event EventHandler? Cancelled;
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);
}
