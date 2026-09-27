using System.Windows;
using System.Windows.Controls;
using StalkerModLauncher.Themes;

namespace StalkerModLauncher.Views.Controls;

public partial class ConflictExplorerContentView : UserControl
{
    public static readonly DependencyProperty UsePdaThemeProperty = DependencyProperty.Register(
        nameof(UsePdaTheme),
        typeof(bool),
        typeof(ConflictExplorerContentView),
        new PropertyMetadata(false, OnUsePdaThemeChanged));

    private ResourceDictionary? _pdaTheme;

    public ConflictExplorerContentView()
    {
        InitializeComponent();
    }

    public bool UsePdaTheme
    {
        get => (bool)GetValue(UsePdaThemeProperty);
        set => SetValue(UsePdaThemeProperty, value);
    }

    private static void OnUsePdaThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((ConflictExplorerContentView)dependencyObject).UpdatePdaTheme((bool)e.NewValue);
    }

    private void UpdatePdaTheme(bool enabled)
    {
        if (enabled && _pdaTheme is null)
        {
            _pdaTheme = new ResourceDictionary
            {
                Source = PdaThemeSelector.CurrentSource
            };
            Resources.MergedDictionaries.Add(_pdaTheme);
        }
        else if (!enabled && _pdaTheme is not null)
        {
            Resources.MergedDictionaries.Remove(_pdaTheme);
            _pdaTheme = null;
        }
    }
}
