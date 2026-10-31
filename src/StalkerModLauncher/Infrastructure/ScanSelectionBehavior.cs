using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StalkerModLauncher.Infrastructure;

internal sealed class ScanSelectionBehavior
{
    private int _anchor = -1;

    public ScanSelectionBehavior(ListView list)
    {
        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewKeyDown += OnKeyDown;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListView)sender;
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(list, source) is not ListViewItem item)
        {
            return;
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (_anchor < 0)
            {
                _anchor = index;
            }

            SelectRange(list, _anchor, index);
            item.Focus();
            e.Handled = true;
        }
        else
        {
            _anchor = index;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ((ListView)sender).SelectAll();
            e.Handled = true;
        }
    }

    internal static void SelectRange(ListView list, int anchor, int index)
    {
        for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
        {
            var item = list.Items[i];
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Add(item);
            }
        }
    }
}
