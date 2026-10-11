using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StalkerModLauncher.Resources;
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
        OrderColumn.Width = compact ? 34 : 38;
        EnabledColumn.Width = compact ? 56 : 60;
        StatusColumn.Width = compact ? 140 : 160;
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
        Resources["Mo2FiltersMargin"] = new Thickness(0, 0, 0, compact ? 6 : 8);
        Resources["Mo2ProblemFilterMargin"] = new Thickness(0, compact ? 6 : 7, 0, 0);
        Resources["Mo2LaunchModeMargin"] = new Thickness(0, compact ? 5 : 7, 0, 0);
        Resources["Mo2LaunchModeFontSize"] = compact ? 10d : 12d;
        Resources["Mo2LaunchModeLabelMargin"] = new Thickness(0, 0, compact ? 8 : 10, 0);
        Resources["Mo2WorkspaceOptionMargin"] = new Thickness(0, 0, compact ? 10 : 14, 0);
        Resources["Mo2LaunchHelpFontSize"] = compact ? 13d : 12d;
        Resources["Mo2StatusMarkerMargin"] = new Thickness(0, 0, compact ? 5 : 6, 0);
        Resources["Mo2CandidateMinHeight"] = compact ? 24d : 31d;
        Resources["Mo2CandidatePadding"] = compact ? new Thickness(5, 2, 5, 2) : new Thickness(9, 5, 9, 5);
        Resources["Mo2CandidatePlaceholderMargin"] = new Thickness(10, 0, compact ? 28 : 30, 0);
        Resources["Mo2FolderBrowseButtonMargin"] = new Thickness(compact ? 6 : 8, 0, 0, 0);
        Resources["Mo2FolderBrowseButtonPadding"] = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 2, 8, 2);
        Resources["Mo2OverwriteOptionMargin"] = new Thickness(0, compact ? 7 : 10, 0, 0);
        Resources["Mo2ContentCheckBoxStyle"] = FindResource(compact ? typeof(CheckBox) : "Mo2CompactCheckBoxStyle");

        var enabledStyle = new Style(typeof(CheckBox), (Style)FindResource(typeof(CheckBox)));
        enabledStyle.Setters.Add(new Setter(ToolTipProperty, Strings.Mo2_EnabledReadOnlyHint));
        enabledStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        enabledStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        EnabledColumn.ElementStyle = enabledStyle;

        var sourceStyle = new Style(typeof(TextBox), (Style)FindResource(typeof(TextBox)));
        var emptySource = new DataTrigger { Binding = new Binding(nameof(Mo2ImportViewModel.HasSource)), Value = false };
        emptySource.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("MutedTextBrush")));
        sourceStyle.Triggers.Add(emptySource);
        Resources["Mo2SourceTextBoxStyle"] = sourceStyle;

        var browseStyle = new Style(typeof(Button), (Style)FindResource(typeof(Button)));
        browseStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var canBrowse = new DataTrigger { Binding = new Binding(nameof(Services.Mo2ImportPreviewEntry.CanBrowseSource)), Value = true };
        canBrowse.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        browseStyle.Triggers.Add(canBrowse);
        Resources["Mo2FolderBrowseButtonStyle"] = browseStyle;
    }
}
