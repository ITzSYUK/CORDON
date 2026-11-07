using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class Mo2ImportContentView : UserControl
{
    public static readonly DependencyProperty UsePdaThemeProperty = DependencyProperty.Register(
        nameof(UsePdaTheme), typeof(bool), typeof(Mo2ImportContentView),
        new PropertyMetadata(false, OnUsePdaThemeChanged));

    private ResourceDictionary? _pdaTheme;

    public Mo2ImportContentView()
    {
        InitializeComponent();
        UpdateAppearance();
    }

    public bool UsePdaTheme
    {
        get => (bool)GetValue(UsePdaThemeProperty);
        set => SetValue(UsePdaThemeProperty, value);
    }

    public IInputElement InitialFocusTarget => SourcePathTextBox;

    private static void OnUsePdaThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((Mo2ImportContentView)dependencyObject).UpdateAppearance();

    private void UpdateAppearance()
    {
        var compact = UsePdaTheme;
        ModList.UsePdaTheme = compact;
        if (compact && _pdaTheme is null)
        {
            _pdaTheme = new ResourceDictionary { Source = PdaThemeSelector.CurrentSource };
            Resources.MergedDictionaries.Add(_pdaTheme);
        }
        else if (!compact && _pdaTheme is not null)
        {
            Resources.MergedDictionaries.Remove(_pdaTheme);
            _pdaTheme = null;
        }

        SourceLabelColumn.Width = new GridLength(compact ? 92 : 115);
        ProfileLabelColumn.Width = new GridLength(compact ? 100 : 110);
        Resources["Mo2SourceButtonMargin"] = new Thickness(compact ? 7 : 8, 0, 0, 0);
        Resources["Mo2DetectedPathsMargin"] = new Thickness(0, 18, 0, compact ? 6 : 8);
        Resources["Mo2RequiredMarkerMargin"] = new Thickness(compact ? 2 : 3, 0, 0, 0);
        Resources["Mo2SourceFieldMargin"] = new Thickness(0, 0, compact ? 7 : 8, 2);
        Resources["Mo2OverwriteFieldMargin"] = new Thickness(0, 0, compact ? 7 : 8, 0);
        Resources["Mo2PathWarningFontSize"] = compact ? 11d : 12d;
        Resources["Mo2PathWarningMargin"] = new Thickness(0, 0, 0, compact ? 5 : 6);
        Resources["Mo2ReadOnlyHintMargin"] = new Thickness(0, compact ? 9 : 16, 0, 0);
        Resources["Mo2PreviewFontSize"] = compact ? 11d : 12d;
        Resources["Mo2SummaryFontSize"] = 12d;
        Resources["Mo2SummaryDetailMargin"] = new Thickness(0, compact ? 2 : 3, 0, 0);
        Resources["Mo2SummaryPanelMargin"] = new Thickness(0, compact ? 7 : 10, 0, compact ? 6 : 8);
        Resources["Mo2LaunchModeMargin"] = new Thickness(0, compact ? 5 : 7, 0, 0);
        Resources["Mo2LaunchModeFontSize"] = compact ? 10d : 12d;
        Resources["Mo2LaunchModeLabelMargin"] = new Thickness(0, 0, compact ? 8 : 10, 0);
        Resources["Mo2WorkspaceOptionMargin"] = new Thickness(0, 0, compact ? 10 : 14, 0);
        Resources["Mo2LaunchHelpFontSize"] = compact ? 13d : 12d;
        Resources["Mo2OverwriteOptionMargin"] = new Thickness(0, compact ? 7 : 10, 0, 0);
        Resources["Mo2ContentCheckBoxStyle"] = FindResource(compact ? typeof(CheckBox) : "Mo2CompactCheckBoxStyle");

        var sourceStyle = new Style(typeof(TextBox), (Style)FindResource(typeof(TextBox)));
        var emptySource = new DataTrigger { Binding = new Binding(nameof(Mo2ImportViewModel.HasSource)), Value = false };
        emptySource.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("MutedTextBrush")));
        sourceStyle.Triggers.Add(emptySource);
        Resources["Mo2SourceTextBoxStyle"] = sourceStyle;
    }
}
