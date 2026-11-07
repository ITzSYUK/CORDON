using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.Themes;

namespace StalkerModLauncher.Views.Controls;

public partial class ModImportListView : UserControl
{
    public static readonly DependencyProperty UsePdaThemeProperty = DependencyProperty.Register(
        nameof(UsePdaTheme), typeof(bool), typeof(ModImportListView), new PropertyMetadata(false, AppearanceChanged));
    public static readonly DependencyProperty IsProfileImportProperty = DependencyProperty.Register(
        nameof(IsProfileImport), typeof(bool), typeof(ModImportListView), new PropertyMetadata(false, AppearanceChanged));
    private ResourceDictionary? _pdaTheme;

    public ModImportListView()
    {
        InitializeComponent();
        UpdateAppearance();
    }

    public bool UsePdaTheme
    {
        get => (bool)GetValue(UsePdaThemeProperty);
        set => SetValue(UsePdaThemeProperty, value);
    }
    public bool IsProfileImport
    {
        get => (bool)GetValue(IsProfileImportProperty);
        set => SetValue(IsProfileImportProperty, value);
    }

    private static void AppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ModImportListView)sender).UpdateAppearance();

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
        OrderColumn.Width = compact ? 34 : 38;
        EnabledColumn.Width = compact ? 56 : 60;
        StatusColumn.Width = compact ? 140 : 160;
        GroupColumn.Header = IsProfileImport ? Strings.ProfileImport_Group : Strings.Mo2_Group;
        ProblemFilter.Content = Strings.Mo2_ShowOnlyProblems;
        Resources["Mo2FiltersMargin"] = new Thickness(0, 0, 0, compact ? 6 : 8);
        Resources["Mo2ProblemFilterMargin"] = new Thickness(0, compact ? 6 : 7, 0, 0);
        Resources["Mo2StatusMarkerMargin"] = new Thickness(0, 0, compact ? 5 : 6, 0);
        Resources["Mo2CandidateMinHeight"] = compact ? 24d : 31d;
        Resources["Mo2CandidatePadding"] = compact ? new Thickness(5, 2, 5, 2) : new Thickness(9, 5, 9, 5);
        Resources["Mo2CandidatePlaceholderMargin"] = new Thickness(10, 0, compact ? 28 : 30, 0);
        Resources["Mo2FolderBrowseButtonMargin"] = new Thickness(compact ? 6 : 8, 0, 0, 0);
        Resources["Mo2FolderBrowseButtonPadding"] = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 2, 8, 2);
        Resources["Mo2ContentCheckBoxStyle"] = FindResource(compact ? typeof(CheckBox) : "Mo2CompactCheckBoxStyle");
        var enabledStyle = new Style(typeof(CheckBox), (Style)FindResource(typeof(CheckBox)));
        enabledStyle.Setters.Add(new Setter(ToolTipProperty, IsProfileImport ? Strings.ProfileImport_EnabledHint : Strings.Mo2_EnabledReadOnlyHint));
        enabledStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        enabledStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        EnabledColumn.ElementStyle = enabledStyle;
        var browseStyle = new Style(typeof(Button), (Style)FindResource(typeof(Button)));
        browseStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var canBrowse = new DataTrigger { Binding = new Binding(nameof(Mo2ImportPreviewEntry.CanBrowseSource)), Value = true };
        canBrowse.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        browseStyle.Triggers.Add(canBrowse);
        Resources["Mo2FolderBrowseButtonStyle"] = browseStyle;
    }
}
