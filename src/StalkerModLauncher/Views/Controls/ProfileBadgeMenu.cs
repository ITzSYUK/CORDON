using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Views.Controls;

internal static class ProfileBadgeMenu
{
    internal static void Open(object sender, FrameworkElement owner, ResourceDictionary? theme = null)
    {
        if (sender is not Button { DataContext: ModProfile { SettingsLoadError.Length: 0 } profile } button)
        {
            return;
        }

        var images = (ProfileBadgeImageConverter)owner.Resources["ProfileBadgeImages"];
        var choices = new (ProfileBadge Badge, string Label)[]
        {
            (ProfileBadge.Default, Strings.Profile_DefaultBadge),
            (ProfileBadge.Loners, Strings.Profile_LonersBadge),
            (ProfileBadge.Bandits, Strings.Profile_BanditsBadge),
            (ProfileBadge.Monolith, Strings.Profile_MonolithBadge),
            (ProfileBadge.Duty, Strings.Profile_DutyBadge),
            (ProfileBadge.Freedom, Strings.Profile_FreedomBadge),
            (ProfileBadge.Mercenaries, Strings.Profile_MercenariesBadge),
            (ProfileBadge.ClearSky, Strings.Profile_ClearSkyBadge),
            (ProfileBadge.Renegades, Strings.Profile_RenegadesBadge),
            (ProfileBadge.Scientists, Strings.Profile_ScientistsBadge)
        };
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            Style = (Style?)owner.TryFindResource(typeof(ContextMenu)),
            Template = (ControlTemplate)owner.FindResource("ProfileBadgeMenuTemplate")
        };
        if (theme is not null)
        {
            menu.Resources.MergedDictionaries.Add(theme);
        }

        menu.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar), (Style?)owner.TryFindResource(typeof(ScrollBar)))
        {
            Setters =
            {
                new Setter(FrameworkElement.WidthProperty, 8d),
                new Setter(FrameworkElement.MinWidthProperty, 8d),
                new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0))
            }
        };

        foreach (var (badge, label) in choices)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Grid { Width = 26, Height = 26 };
            if (badge == ProfileBadge.Default)
            {
                icon.Children.Add(new ContentControl
                {
                    Content = profile,
                    ContentTemplate = (DataTemplate)owner.Resources["DefaultProfileBadge"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Focusable = false
                });
            }
            else
            {
                var image = new Image { Source = images.GetImage(badge) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                icon.Children.Add(image);
            }
            header.Children.Add(icon);
            header.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = new MenuItem
            {
                Header = header,
                Height = 38,
                IsCheckable = true,
                IsChecked = profile.Badge == badge,
                FontWeight = profile.Badge == badge ? FontWeights.Bold : FontWeights.Normal
            };
            AutomationProperties.SetName(item, label);
            item.Click += (_, _) => profile.Badge = badge;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
