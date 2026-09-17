using System.Reflection;
using StalkerModLauncher.Resources;
using System.Windows.Controls;

namespace StalkerModLauncher.Views.Controls;

public partial class PdaAboutView : UserControl
{
    public PdaAboutView()
    {
        InitializeComponent();
        VersionText.Text = LocalizedText.Format(Strings.About_VersionFormat, GetVersion());
    }

    private static string GetVersion()
    {
        var assembly = typeof(PdaAboutView).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
               ?? assembly.GetName().Version?.ToString(3)
               ?? Strings.Common_Unknown;
    }
}
