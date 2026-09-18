using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Themes;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views;

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
    private Border? _dropTargetGroupHeader;
    private bool _dropAfter;
    private bool _preserveSelectionForPotentialDrag;
    private bool _dragInProgress;
    private ResourceDictionary? _pdaTheme;
    private Action<string>? _completeGroupNamePrompt;

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
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var groupHeader = FindAncestor<Border>(source, "ModGroupHeader");
        if (groupHeader?.DataContext is ModEntry { ShowsGroupHeader: true } groupMod)
        {
            if (!IsModListFiltered())
            {
                ViewModel?.SetModGroupCollapsed(groupMod.GroupName, !groupMod.IsGroupCollapsed);
            }

            e.Handled = true;
            return;
        }

        if (IsInteractiveDragSource(source))
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

        var groupHeader = FindAncestor<Border>(source, "ModGroupHeader");
        if (groupHeader?.DataContext is ModEntry { ShowsGroupHeader: true })
        {
            ModGroupHeader_OnPreviewMouseRightButtonDown(groupHeader, e);
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
        var groupName = selectedMods.FirstOrDefault()?.GroupName ?? string.Empty;
        var canMoveWithinGroup = canEdit && groupName.Length > 0 &&
            selectedMods.All(selected => selected.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Conflict_Details,
            ViewModel?.ShowSelectedModConflictsCommand.CanExecute(mod) == true,
            () =>
            {
                ViewModel?.ShowSelectedModConflictsCommand.Execute(mod);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Mod_CreateGroup,
            canEdit,
            () => CreateModGroup(selectedMods, contextMenu)));
        AddMoveToGroupItems(contextMenu, selectedMods, canEdit);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Mod_MoveToGroupStart,
            canMoveWithinGroup,
            () => MoveSelectedModsWithinGroup(selectedMods, moveToEnd: false, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Mod_MoveToGroupEnd,
            canMoveWithinGroup,
            () => MoveSelectedModsWithinGroup(selectedMods, moveToEnd: true, contextMenu)));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveFirst,
            canEdit,
            () => MoveSelectedMods(selectedMods, moveToEnd: false, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveLast,
            canEdit,
            () => MoveSelectedMods(selectedMods, moveToEnd: true, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_Remove,
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
            (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
             Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        var draggedMods = ModsList.SelectedItems.Contains(_draggedMod)
            ? GetSelectedModsInProfileOrder()
            : [_draggedMod];
        if (draggedMods.Count == 0)
        {
            return;
        }

        try
        {
            _dragInProgress = true;
            DragDrop.DoDragDrop(
                ModsList,
                new ModDragPayload(draggedMods),
                DragDropEffects.Move);
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

        var groupHeader = FindAncestor<Border>(source, "ModGroupHeader");
        var target = groupHeader is null ? FindAncestor<ListViewItem>(source) : null;
        var dropTarget = (FrameworkElement?)groupHeader ?? target;
        var dropAfter = dropTarget is not null && e.GetPosition(dropTarget).Y > dropTarget.ActualHeight / 2;
        if (target == _dropTargetItem && groupHeader == _dropTargetGroupHeader && dropAfter == _dropAfter)
        {
            return;
        }

        ClearDropHighlight();
        _dropTargetItem = target;
        _dropTargetGroupHeader = groupHeader;
        _dropAfter = dropAfter;
        SetDropHighlight(_dropTargetItem, _dropTargetGroupHeader, dropAfter);
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
            var targetGroupName = (_dropTargetGroupHeader?.DataContext as ModEntry)?.GroupName;
            var target = _dropTargetItem?.DataContext as ModEntry;

            try
            {
                if (target is not null)
                {
                    var targetIndex = ViewModel.SelectedProfile?.Mods.IndexOf(target) ?? -1;
                    if (targetIndex >= 0)
                    {
                        ViewModel.MoveModsToInsertionIndex(
                            payload.Mods,
                            targetIndex + (_dropAfter ? 1 : 0),
                            target.GroupName,
                            preserveGroups: false);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(targetGroupName))
                {
                    ViewModel.MoveModsToGroup(payload.Mods, targetGroupName);
                }
                else
                {
                    ViewModel.MoveModsToInsertionIndex(
                        payload.Mods,
                        ViewModel.SelectedProfile?.Mods.Count ?? 0,
                        string.Empty,
                        preserveGroups: false);
                }

                RestoreSelection(payload.Mods, scrollIntoView: false);
            }
            finally
            {
                ClearDropHighlight();
            }
            e.Handled = true;
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

        if (_dropTargetGroupHeader is not null)
        {
            _dropTargetGroupHeader.BorderBrush = (Brush)FindResource("StrokeBrush");
            _dropTargetGroupHeader.BorderThickness = new Thickness(0, 1, 0, 1);
        }

        _dropTargetItem = null;
        _dropTargetGroupHeader = null;
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

    private void MoveSelectedModsWithinGroup(
        IReadOnlyList<ModEntry> mods,
        bool moveToEnd,
        ContextMenu contextMenu)
    {
        ViewModel?.MoveModsWithinGroupToBoundary(mods, moveToEnd);
        RestoreSelection(mods);
        contextMenu.IsOpen = false;
    }

    private void RestoreSelection(IReadOnlyList<ModEntry> mods, bool scrollIntoView = true)
    {
        ModsList.SelectedItems.Clear();
        foreach (var mod in mods)
        {
            ModsList.SelectedItems.Add(mod);
        }

        if (scrollIntoView && mods.Count > 0)
        {
            ModsList.ScrollIntoView(mods[^1]);
        }
    }

    private void AddMoveToGroupItems(
        ContextMenu contextMenu,
        IReadOnlyList<ModEntry> mods,
        bool canEdit)
    {
        const int visibleGroupCount = 5;
        var groupNames = ViewModel?.GetModGroupNames() ?? [];
        var offset = 0;
        var destinationItems = new List<MenuItem>();
        MenuItem? previousItem = null;
        MenuItem? nextItem = null;

        void RefreshItems()
        {
            for (var index = 0; index < destinationItems.Count; index++)
            {
                destinationItems[index].Header = $"{Strings.Mod_MoveToGroup}: {groupNames[offset + index]}";
            }

            previousItem!.IsEnabled = canEdit && offset > 0;
            nextItem!.IsEnabled = canEdit && offset + destinationItems.Count < groupNames.Count;
        }

        void Scroll(int direction)
        {
            offset = Math.Clamp(
                offset + direction,
                0,
                Math.Max(0, groupNames.Count - destinationItems.Count));
            RefreshItems();
        }

        contextMenu.Items.Add(CreateLeftClickMenuItem(
            $"{Strings.Mod_MoveToGroup}: {Strings.Mod_NoGroup}",
            canEdit,
            () =>
            {
                ViewModel?.MoveModsToGroup(mods, string.Empty);
                contextMenu.IsOpen = false;
            }));
        previousItem = CreateLeftClickMenuItem("▲", false, () => Scroll(-1));
        previousItem.HorizontalContentAlignment = HorizontalAlignment.Center;
        previousItem.ToolTip = Strings.Common_MoveUp;
        AutomationProperties.SetName(previousItem, Strings.Common_MoveUp);
        contextMenu.Items.Add(previousItem);
        for (var index = 0; index < Math.Min(visibleGroupCount, groupNames.Count); index++)
        {
            var visibleIndex = index;
            var item = CreateLeftClickMenuItem(
                string.Empty,
                canEdit,
                () =>
                {
                    var groupName = groupNames[offset + visibleIndex];
                    ViewModel?.MoveModsToGroup(mods, groupName);
                    contextMenu.IsOpen = false;
                });
            destinationItems.Add(item);
            contextMenu.Items.Add(item);
        }

        nextItem = CreateLeftClickMenuItem("▼", false, () => Scroll(1));
        nextItem.HorizontalContentAlignment = HorizontalAlignment.Center;
        nextItem.ToolTip = Strings.Common_MoveDown;
        AutomationProperties.SetName(nextItem, Strings.Common_MoveDown);
        contextMenu.Items.Add(nextItem);
        contextMenu.PreviewMouseWheel += (_, e) =>
        {
            if (groupNames.Count > visibleGroupCount)
            {
                Scroll(e.Delta > 0 ? -1 : 1);
                e.Handled = true;
            }
        };
        RefreshItems();
    }

    private void CreateModGroup(IReadOnlyList<ModEntry> mods, ContextMenu contextMenu)
    {
        contextMenu.IsOpen = false;
        ShowGroupNamePrompt(Strings.Mod_CreateGroup, string.Empty, name =>
        {
            if (ViewModel?.CreateModGroup(mods, name) != true)
            {
                ViewModel?.DialogService.ShowError(Strings.Mod_CreateGroup, Strings.Mod_GroupNameExists);
            }
        });
    }

    private void ModGroupHeader_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border header ||
            header.DataContext is not ModEntry { ShowsGroupHeader: true } groupMod)
        {
            return;
        }

        var groupName = groupMod.GroupName;
        var canEdit = ViewModel?.CanEditSelectedProfile == true;
        var groupNames = ViewModel?.GetModGroupNames() ?? [];
        var canMoveUp = canEdit && groupNames.Count > 0 &&
            !groupNames[0].Equals(groupName, StringComparison.OrdinalIgnoreCase);
        var canMoveDown = canEdit && groupNames.Count > 0 &&
            !groupNames[groupNames.Count - 1].Equals(groupName, StringComparison.OrdinalIgnoreCase);
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Mod_RenameGroup,
            canEdit,
            () => RenameModGroup(groupName, contextMenu)));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Mod_DeleteGroup,
            canEdit,
            () =>
            {
                ViewModel?.DeleteModGroup(groupName);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveUp,
            canMoveUp,
            () =>
            {
                ViewModel?.MoveModGroupByOffset(groupName, -1);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveDown,
            canMoveDown,
            () =>
            {
                ViewModel?.MoveModGroupByOffset(groupName, 1);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveFirst,
            canEdit,
            () =>
            {
                ViewModel?.MoveModGroupToStart(groupName);
                contextMenu.IsOpen = false;
            }));
        contextMenu.Items.Add(CreateLeftClickMenuItem(
            Strings.Common_MoveLast,
            canEdit,
            () =>
            {
                ViewModel?.MoveModGroupToEnd(groupName);
                contextMenu.IsOpen = false;
            }));
        contextMenu.PlacementTarget = header;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void RenameModGroup(string oldName, ContextMenu contextMenu)
    {
        contextMenu.IsOpen = false;
        ShowGroupNamePrompt(Strings.Mod_RenameGroup, oldName, name =>
        {
            if (ViewModel?.RenameModGroup(oldName, name) != true)
            {
                ViewModel?.DialogService.ShowError(Strings.Mod_RenameGroup, Strings.Mod_GroupNameExists);
            }
        });
    }

    private void ShowGroupNamePrompt(string title, string initialValue, Action<string> complete)
    {
        if (!UsePdaTheme)
        {
            var dialog = new TextPromptWindow(title, Strings.Mod_GroupNamePrompt, initialValue)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                complete(dialog.Value);
            }

            return;
        }

        _completeGroupNamePrompt = complete;
        GroupNamePromptTextBox.Text = initialValue;
        GroupNamePromptPanel.Visibility = Visibility.Visible;
        GroupNamePromptTextBox.Focus();
        GroupNamePromptTextBox.SelectAll();
    }

    private void CompleteGroupNamePrompt_OnClick(object sender, RoutedEventArgs e) => CompleteGroupNamePrompt();

    private void CancelGroupNamePrompt_OnClick(object sender, RoutedEventArgs e) => HideGroupNamePrompt();

    private void GroupNamePromptTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CompleteGroupNamePrompt();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideGroupNamePrompt();
            e.Handled = true;
        }
    }

    private void CompleteGroupNamePrompt()
    {
        var name = GroupNamePromptTextBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var complete = _completeGroupNamePrompt;
        HideGroupNamePrompt();
        complete?.Invoke(name);
    }

    private void HideGroupNamePrompt()
    {
        _completeGroupNamePrompt = null;
        GroupNamePromptPanel.Visibility = Visibility.Collapsed;
        GroupNamePromptTextBox.Clear();
    }

    private bool IsModListFiltered() => ViewModel is { } viewModel &&
                                        (!string.IsNullOrWhiteSpace(viewModel.ModSearchText) ||
                                         viewModel.SelectedModFilter != ModListFilter.All);

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

    private static void SetDropHighlight(FrameworkElement? item, Border? groupHeader, bool after)
    {
        if (groupHeader is not null)
        {
            groupHeader.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0x00));
            groupHeader.BorderThickness = after ? new Thickness(0, 1, 0, 2) : new Thickness(0, 2, 0, 1);
            return;
        }

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

    private static T? FindAncestor<T>(DependencyObject current, string name) where T : FrameworkElement
    {
        while (current is not null)
        {
            if (current is T typed && typed.Name == name)
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
