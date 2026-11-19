using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StalkerModLauncher.Models;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class PdaProfileDrawerView : UserControl
{
    private Point _dragStartPoint;
    private ModProfile? _draggedProfile;
    private ListBoxItem? _dropTargetItem;
    private bool _dropAfter;

    public PdaProfileDrawerView()
    {
        InitializeComponent();
    }

    public event EventHandler? ProfileSelected;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ProfileBadgeButton_OnClick(object sender, RoutedEventArgs e) =>
        ProfileBadgeMenu.Open(sender, this, Resources.MergedDictionaries[0]);

    private void ProfilesList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedProfile = null;
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        _draggedProfile = FindAncestor<ListBoxItem>(source)?.DataContext as ModProfile;
        _dragStartPoint = e.GetPosition(ProfilesList);
    }

    private void ProfilesList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedProfile is null || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var releasedProfile = FindAncestor<ListBoxItem>(source)?.DataContext as ModProfile;
        var shouldActivate = ReferenceEquals(releasedProfile, _draggedProfile);
        _draggedProfile = null;

        if (shouldActivate)
        {
            ProfileSelected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ProfilesList_OnMouseMove(object sender, MouseEventArgs e)
    {
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
            DragDrop.DoDragDrop(ProfilesList, draggedProfile, DragDropEffects.Move);
        }
        finally
        {
            _draggedProfile = null;
            ClearDropHighlight();
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
        var target = e.OriginalSource is DependencyObject source ? FindAncestor<ListBoxItem>(source) : null;
        var dropAfter = target is not null && e.GetPosition(target).Y > target.ActualHeight / 2;
        if (target == _dropTargetItem && dropAfter == _dropAfter)
        {
            return;
        }

        ClearDropHighlight();
        _dropTargetItem = target;
        _dropAfter = dropAfter;
        SetDropHighlight(target, dropAfter);
    }

    private void ProfilesList_OnDragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

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
            chrome.BorderThickness = new Thickness(0, 1, 0, 1);
        }

        _dropTargetItem = null;
        _dropAfter = false;
    }

    private static void SetDropHighlight(DependencyObject? item, bool after)
    {
        var chrome = item is null ? null : FindVisualChild<Border>(item, "ItemChrome");
        if (chrome is null)
        {
            return;
        }

        chrome.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0x9A, 0x2B));
        chrome.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
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
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed && (childName is null || typed.Name == childName))
            {
                return typed;
            }

            var nested = FindVisualChild<T>(child, childName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
