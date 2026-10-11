using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class FomodInstallerContentView : UserControl
{
    public static readonly DependencyProperty UsePdaThemeProperty = DependencyProperty.Register(
        nameof(UsePdaTheme), typeof(bool), typeof(FomodInstallerContentView),
        new PropertyMetadata(false, (owner, _) => ((FomodInstallerContentView)owner).UpdateTheme()));

    private ResourceDictionary? _pdaTheme;

    public FomodInstallerContentView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is FomodInstallerViewModel oldModel) oldModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (args.NewValue is FomodInstallerViewModel newModel) newModel.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    public bool UsePdaTheme
    {
        get => (bool)GetValue(UsePdaThemeProperty);
        set => SetValue(UsePdaThemeProperty, value);
    }

    private void UpdateTheme()
    {
        if (UsePdaTheme && _pdaTheme is null)
        {
            _pdaTheme = new ResourceDictionary { Source = PdaThemeSelector.CurrentSource };
            Resources.MergedDictionaries.Add(_pdaTheme);
        }
        else if (!UsePdaTheme && _pdaTheme is not null)
        {
            Resources.MergedDictionaries.Remove(_pdaTheme);
            _pdaTheme = null;
        }
    }

    private void Option_OnPreview(object sender, RoutedEventArgs e)
    {
        if (DataContext is FomodInstallerViewModel wizard && sender is FrameworkElement { DataContext: FomodInstallerOption option })
            wizard.PreviewOption = option;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FomodInstallerViewModel.CurrentStep)) OptionsScrollViewer.ScrollToTop();
    }
}
