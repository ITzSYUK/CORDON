using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StalkerModLauncher.Models;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Views.Controls;

public partial class ModPanelView : UserControl
{
    public static readonly DependencyProperty ShowStandaloneToggleProperty = DependencyProperty.Register(
        nameof(ShowStandaloneToggle),
        typeof(bool),
        typeof(ModPanelView),
        new PropertyMetadata(true));

    public static readonly DependencyProperty UseCompactHeaderProperty = DependencyProperty.Register(
        nameof(UseCompactHeader),
        typeof(bool),
        typeof(ModPanelView),
        new PropertyMetadata(false));

    public static readonly DependencyProperty UsePdaThemeProperty = DependencyProperty.Register(
        nameof(UsePdaTheme),
        typeof(bool),
        typeof(ModPanelView),
        new PropertyMetadata(false, OnUsePdaThemeChanged));

    private sealed record ModDragPayload(IReadOnlyList<ModEntry> Mods);

    private Point _dragStartPoint;
    private ModEntry? _draggedMod;
    private ListViewItem? _dropTargetItem;
    private bool _dropAfter;
    private bool _preserveSelectionForPotentialDrag;
    private bool _dragInProgress;
    private ResourceDictionary? _pdaTheme;

    public ModPanelView()
    {
        InitializeComponent();
    }

    public bool ShowStandaloneToggle
    {
        get => (bool)GetValue(ShowStandaloneToggleProperty);
        set => SetValue(ShowStandaloneToggleProperty, value);
    }

    public bool UseCompactHeader
    {
        get => (bool)GetValue(UseCompactHeaderProperty);
        set => SetValue(UseCompactHeaderProperty, value);
    }

    public bool UsePdaTheme
    {
        get => (bool)GetValue(UsePdaThemeProperty);
        set => SetValue(UsePdaThemeProperty, value);
    }

    private static void OnUsePdaThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((ModPanelView)dependencyObject).UpdatePdaTheme((bool)e.NewValue);
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

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ModsList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedMod = null;
        if (e.OriginalSource is not DependencyObject source || IsInteractiveDragSource(source))
        {
            return;
        }

        var item = FindAncestor<ListViewItem>(source);
        _draggedMod = item?.DataContext as ModEntry;
        _dragStartPoint = e.GetPosition(ModsList);

        // WPF normally collapses an extended selection as soon as an already
        // selected row is pressed. Keep it intact long enough to start a group drag.
        _preserveSelectionForPotentialDrag = item is { IsSelected: true } &&
                                             ModsList.SelectedItems.Count > 1 &&
                                             Keyboard.Modifiers == ModifierKeys.None;
        if (_preserveSelectionForPotentialDrag)
        {
            item!.Focus();
            e.Handled = true;
        }
    }

    private void ModsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_preserveSelectionForPotentialDrag && !_dragInProgress && _draggedMod is not null)
        {
            var clickedMod = _draggedMod;
            ModsList.SelectedItems.Clear();
            ModsList.SelectedItem = clickedMod;
        }

        _preserveSelectionForPotentialDrag = false;
        _draggedMod = null;
    }

    private void ModsList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var item = FindAncestor<ListViewItem>(source);
        if (item?.DataContext is not ModEntry mod)
        {
            return;
        }

        if (!ModsList.SelectedItems.Contains(mod))
        {
            ModsList.SelectedItem = mod;
        }

        var selectedMods = GetSelectedModsInProfileOrder();
        var canEdit = ViewModel?.CanEditSelectedProfile == true;
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            "Подробности конфликтов",
            ViewModel?.ShowSelectedModConflictsCommand.CanExecute(mod) == true,
            () =>
            {
                ViewModel?.ShowSelectedModConflictsCommand.Execute(mod);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            "В начало",
            canEdit,
            () => MoveSelectedMods(selectedMods, moveToEnd: false, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            "В конец",
            canEdit,
            () => MoveSelectedMods(selectedMods, moveToEnd: true, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            "Убрать",
            canEdit,
            () =>
            {
                ViewModel?.RemoveMods(selectedMods);
                contextMenu.IsOpen = false;
            }));
        contextMenu.PlacementTarget = item;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void ModsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInteractiveDragSource(source))
        {
            return;
        }

        var mod = FindAncestor<ListViewItem>(source)?.DataContext as ModEntry;
        if (mod is not null && ViewModel?.ShowSelectedModConflictsCommand.CanExecute(mod) == true)
        {
            ViewModel.ShowSelectedModConflictsCommand.Execute(mod);
            e.Handled = true;
        }
    }

    private void ModsList_OnMouseMove(object sender, MouseEventArgs e)
    {
        var currentPosition = e.GetPosition(ModsList);
        if (ViewModel?.CanEditSelectedProfile != true ||
            e.LeftButton != MouseButtonState.Pressed ||
            _draggedMod is null ||
            Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedMod = _draggedMod;
        var draggedMods = ModsList.SelectedItems.Contains(draggedMod)
            ? GetSelectedModsInProfileOrder()
            : [draggedMod];
        try
        {
            _dragInProgress = true;
            DragDrop.DoDragDrop(ModsList, new ModDragPayload(draggedMods), DragDropEffects.Move);
        }
        finally
        {
            _dragInProgress = false;
            _preserveSelectionForPotentialDrag = false;
            _draggedMod = null;
            ClearDropHighlight();
        }
    }

    private void ModsList_OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel?.CanEditSelectedProfile != true)
        {
            ClearDropHighlight();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ClearDropHighlight();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            AutoScroll(e.GetPosition(ModsList));
            return;
        }

        if (!e.Data.GetDataPresent(typeof(ModDragPayload)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        AutoScroll(e.GetPosition(ModsList));

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var target = FindAncestor<ListViewItem>(source);
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

    private void ModsList_OnDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
    }

    private void ModsList_OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel?.CanEditSelectedProfile != true)
        {
            ClearDropHighlight();
            return;
        }

        if (e.Data.GetDataPresent(typeof(ModDragPayload)))
        {
            var payload = (ModDragPayload)e.Data.GetData(typeof(ModDragPayload))!;
            var source = e.OriginalSource as DependencyObject;
            var target = source is null
                ? null
                : FindAncestor<ListViewItem>(source)?.DataContext as ModEntry;

            if (target is not null)
            {
                var targetIndex = ViewModel.SelectedProfile?.Mods.IndexOf(target) ?? -1;
                if (targetIndex >= 0)
                {
                    ViewModel.MoveModsToInsertionIndex(payload.Mods, targetIndex + (_dropAfter ? 1 : 0));
                }
            }
            else
            {
                ViewModel.MoveModsToInsertionIndex(payload.Mods, ViewModel.SelectedProfile?.Mods.Count ?? 0);
            }

            RestoreSelection(payload.Mods);
            ClearDropHighlight();
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ViewModel.AddDroppedMods((string[])e.Data.GetData(DataFormats.FileDrop)!);
        }

        ClearDropHighlight();
    }

    private void ClearDropHighlight()
    {
        var chrome = _dropTargetItem is null ? null : FindVisualChild<Border>(_dropTargetItem, "RowChrome");
        if (chrome is not null)
        {
            chrome.BorderBrush = Brushes.Transparent;
            chrome.BorderThickness = new Thickness(1);
        }

        _dropTargetItem = null;
        _dropAfter = false;
    }

    private List<ModEntry> GetSelectedModsInProfileOrder()
    {
        var selected = ModsList.SelectedItems.Cast<ModEntry>().ToHashSet();
        return ViewModel?.SelectedProfile?.Mods.Where(selected.Contains).ToList() ?? [];
    }

    private void MoveSelectedMods(
        IReadOnlyList<ModEntry> mods,
        bool moveToEnd,
        ContextMenu contextMenu)
    {
        if (moveToEnd)
        {
            ViewModel?.MoveModsToEnd(mods);
        }
        else
        {
            ViewModel?.MoveModsToStart(mods);
        }

        RestoreSelection(mods);
        contextMenu.IsOpen = false;
    }

    private void RestoreSelection(IReadOnlyList<ModEntry> mods)
    {
        ModsList.SelectedItems.Clear();
        foreach (var mod in mods)
        {
            ModsList.SelectedItems.Add(mod);
        }

        if (mods.Count > 0)
        {
            ModsList.ScrollIntoView(mods[^1]);
        }
    }

    private static MenuItem CreateLeftClickMenuItem(string header, bool isEnabled, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        var armed = false;
        item.PreviewMouseDown += (_, args) =>
        {
            armed = args.ChangedButton == MouseButton.Left;
            if (armed)
            {
                item.CaptureMouse();
            }

            args.Handled = true;
        };
        item.PreviewMouseUp += (_, args) =>
        {
            var shouldInvoke = args.ChangedButton == MouseButton.Left && armed && item.IsMouseOver;
            armed = false;
            item.ReleaseMouseCapture();
            args.Handled = true;
            if (shouldInvoke)
            {
                action();
            }
        };
        return item;
    }

    private static void SetDropHighlight(FrameworkElement? item, bool after)
    {
        var chrome = item is null ? null : FindVisualChild<Border>(item, "RowChrome");
        if (chrome is null)
        {
            return;
        }

        chrome.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0x00));
        chrome.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void AutoScroll(Point position)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(ModsList);
        if (scrollViewer is null)
        {
            return;
        }

        const double edge = 32;
        if (position.Y < edge)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - 18);
        }
        else if (position.Y > ModsList.ActualHeight - edge)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + 18);
        }
    }

    private void ModsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListView listView || listView.View is not GridView gridView || gridView.Columns.Count < 4)
        {
            return;
        }

        var fixedWidth = gridView.Columns[0].Width +
                         gridView.Columns[1].Width +
                         gridView.Columns[2].Width +
                         SystemParameters.VerticalScrollBarWidth;
        const double layoutSafetyMargin = 4;
        var available = listView.ActualWidth - fixedWidth - layoutSafetyMargin;
        if (available > 80)
        {
            gridView.Columns[3].Width = available;
        }
    }

    private static bool IsInteractiveDragSource(DependencyObject source)
    {
        return FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
               FindAncestor<TextBox>(source) is not null ||
               FindAncestor<Grid>(source) is { Name: "ActionRail" };
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
