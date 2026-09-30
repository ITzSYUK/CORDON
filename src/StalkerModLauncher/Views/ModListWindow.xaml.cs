using System.ComponentModel;
using System.Windows;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views;

public partial class ModListWindow : Window
{
    private ResourceDictionary? _pdaTheme;

    public ModListWindow(MainViewModel viewModel, bool usePdaTheme)
    {
        InitializeComponent();
        UsePdaTheme = usePdaTheme;
        UpdatePdaTheme(usePdaTheme);
        ModListPanel.UsePdaTheme = usePdaTheme;
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        UpdateTitle();
    }

    public bool UsePdaTheme { get; }

    public static void Show(Window owner, MainViewModel viewModel, bool usePdaTheme)
    {
        foreach (var window in Application.Current.Windows.OfType<ModListWindow>())
        {
            if (ReferenceEquals(window.DataContext, viewModel))
            {
                if (window.UsePdaTheme == usePdaTheme)
                {
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    window.Activate();
                    return;
                }
            }
        }

        var modListWindow = new ModListWindow(viewModel, usePdaTheme)
        {
            Owner = owner
        };
        modListWindow.Show();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.HasProfiles))
        {
            Dispatcher.BeginInvoke(UpdateTitle);
        }
    }

    private void UpdateTitle()
    {
        var profileName = ViewModel?.SelectedProfile?.Name;
        Title = string.IsNullOrWhiteSpace(profileName)
            ? Strings.Mod_ListTitle
            : LocalizedText.Format(Strings.Mod_ListWindowTitleFormat, profileName);
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

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!UsePdaTheme)
        {
            WindowSystemIntegrationService.Initialize(this);
            return;
        }

        WindowSystemIntegrationService.Initialize(
            this,
            PdaThemeSelector.UseNewTheme
                ? WindowSystemIntegrationService.NewPdaFrameColors
                : WindowSystemIntegrationService.PdaFrameColors);
    }
}
