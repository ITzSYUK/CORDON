using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class ProfileSidebarView : UserControl
{
    public static readonly DependencyProperty IsTrayModeProperty = DependencyProperty.Register(
        nameof(IsTrayMode),
        typeof(bool),
        typeof(ProfileSidebarView),
        new PropertyMetadata(false, OnIsTrayModeChanged));

    public static readonly DependencyProperty PrimaryProfileActionCommandProperty = DependencyProperty.Register(
        nameof(PrimaryProfileActionCommand),
        typeof(ICommand),
        typeof(ProfileSidebarView));

    private Point _dragStartPoint;
    private ModProfile? _draggedProfile;
    private ListBoxItem? _dropTargetItem;
    private bool _dropAfter;

    public ProfileSidebarView()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? ModCatalogRequested;
    public event RoutedEventHandler? LauncherSettingsRequested;
    public event RoutedEventHandler? OpenLauncherRequested;

    public bool IsTrayMode
    {
        get => (bool)GetValue(IsTrayModeProperty);
        set => SetValue(IsTrayModeProperty, value);
    }

    public ICommand? PrimaryProfileActionCommand
    {
        get => (ICommand?)GetValue(PrimaryProfileActionCommandProperty);
        set => SetValue(PrimaryProfileActionCommandProperty, value);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ProfileBadgeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModProfile { SettingsLoadError.Length: 0 } profile } button)
        {
            return;
        }

        var images = (ProfileBadgeImageConverter)Resources["ProfileBadgeImages"];
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
            Style = (Style)Resources["ProfileBadgeMenuStyle"]
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
                    ContentTemplate = (DataTemplate)Resources["DefaultProfileBadge"],
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

    private void ModCatalogButton_OnClick(object sender, RoutedEventArgs e)
    {
        ModCatalogRequested?.Invoke(this, e);
    }

    private void LauncherSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        LauncherSettingsRequested?.Invoke(this, e);
    }

    private void OpenLauncherButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenLauncherRequested?.Invoke(this, e);
    }

    private void ProfilesList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsTrayMode)
        {
            if (e.OriginalSource is DependencyObject traySource &&
                FindAncestor<ListBoxItem>(traySource) is not null &&
                FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(traySource) is null)
            {
                e.Handled = true;
            }

            return;
        }

        _draggedProfile = null;
        if (e.OriginalSource is not DependencyObject source || IsInteractiveDragSource(source))
        {
            return;
        }

        _draggedProfile = FindAncestor<ListBoxItem>(source)?.DataContext as ModProfile;
        _dragStartPoint = e.GetPosition(ProfilesList);
    }

    private void ProfilesList_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (IsTrayMode)
        {
            return;
        }

        var currentPosition = e.GetPosition(ProfilesList);
        if (e.LeftButton != MouseButtonState.Pressed ||
            _draggedProfile is null ||
            Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedProfile = _draggedProfile;
        try
        {
            ProfilesList.SelectedItem = draggedProfile;
            DragDrop.DoDragDrop(ProfilesList, draggedProfile, DragDropEffects.Move);
        }
        finally
        {
            _draggedProfile = null;
            ClearDropHighlight();
        }
    }

    private static void OnIsTrayModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ProfileSidebarView view)
        {
            view.ProfilesList.AllowDrop = e.NewValue is not true;
        }
    }

    private void ProfilesList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ModProfile)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        AutoScroll(e.GetPosition(ProfilesList));

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>(source);
        var dropAfter = target is not null && e.GetPosition(target).Y > target.ActualHeight / 2;
        if (target == _dropTargetItem && dropAfter == _dropAfter)
        {
            return;
        }

        ClearDropHighlight();
        _dropTargetItem = target;
        _dropAfter = dropAfter;
        SetDropHighlight(_dropTargetItem, dropAfter);
    }

    private void ProfilesList_OnDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
    }

    private void ProfilesList_OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.GetDataPresent(typeof(ModProfile)))
        {
            ClearDropHighlight();
            return;
        }

        var profile = (ModProfile)e.Data.GetData(typeof(ModProfile))!;
        var target = e.OriginalSource is DependencyObject source
            ? FindAncestor<ListBoxItem>(source)?.DataContext as ModProfile
            : null;
        var targetIndex = target is null ? ViewModel.Profiles.Count : ViewModel.Profiles.IndexOf(target);
        ViewModel.MoveProfileToInsertionIndex(profile, targetIndex + (target is not null && _dropAfter ? 1 : 0));
        ClearDropHighlight();
    }

    private void AutoScroll(Point position)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(ProfilesList);
        if (scrollViewer is null)
        {
            return;
        }

        const double edge = 32;
        if (position.Y < edge)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - 18);
        }
        else if (position.Y > ProfilesList.ActualHeight - edge)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + 18);
        }
    }

    private void ClearDropHighlight()
    {
        var chrome = _dropTargetItem is null ? null : FindVisualChild<Border>(_dropTargetItem, "ItemChrome");
        if (chrome is not null)
        {
            chrome.BorderBrush = Brushes.Transparent;
            chrome.BorderThickness = new Thickness(0);
        }

        _dropTargetItem = null;
        _dropAfter = false;
    }

    private static void SetDropHighlight(FrameworkElement? item, bool after)
    {
        var chrome = item is null ? null : FindVisualChild<Border>(item, "ItemChrome");
        if (chrome is null)
        {
            return;
        }

        chrome.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0x00));
        chrome.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private static bool IsInteractiveDragSource(DependencyObject source)
    {
        return FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
               FindAncestor<TextBox>(source) is not null;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? childName = null) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed && (childName is null || typed.Name == childName))
            {
                return typed;
            }

            var found = FindVisualChild<T>(child, childName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
