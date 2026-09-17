using System.Reflection;
using System.Windows;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Views;

public partial class AboutWindow : Window
{
    public static readonly DependencyProperty DontShowAgainProperty =
        DependencyProperty.Register(nameof(DontShowAgain), typeof(bool), typeof(AboutWindow), new PropertyMetadata(false));

    public bool DontShowAgain
    {
        get => (bool)GetValue(DontShowAgainProperty);
        set => SetValue(DontShowAgainProperty, value);
    }

    public AboutWindow()
    {
        InitializeComponent();
        VersionTextBlock.Text = GetVersionText();
    }

    private static string GetVersionText()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informationalVersion?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? Strings.Common_Unknown;
        return LocalizedText.Format(Strings.About_VersionFormat, version);
    }

    private void AboutWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowSystemIntegrationService.Initialize(this);
    }

}
