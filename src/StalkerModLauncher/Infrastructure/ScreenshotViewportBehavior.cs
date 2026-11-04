using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Infrastructure;

public static class ScreenshotViewportBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScreenshotViewportBehavior), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(ScreenshotViewportBehavior));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if ((bool)args.NewValue)
        {
            viewer.ScrollChanged += OnScrollChanged;
            viewer.Loaded += OnLoaded;
            viewer.SizeChanged += OnSizeChanged;
            viewer.IsVisibleChanged += OnVisibilityChanged;
            viewer.Unloaded += OnUnloaded;
        }
        else
        {
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.Loaded -= OnLoaded;
            viewer.SizeChanged -= OnSizeChanged;
            viewer.IsVisibleChanged -= OnVisibilityChanged;
            viewer.Unloaded -= OnUnloaded;
            Release(viewer);
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs args) => Schedule((ScrollViewer)sender);
    private static void OnLoaded(object sender, RoutedEventArgs args) => Schedule((ScrollViewer)sender);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) => Schedule((ScrollViewer)sender);
    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Schedule((ScrollViewer)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args) => Release((ScrollViewer)sender);

    private static void Schedule(ScrollViewer viewer)
    {
        if ((bool)viewer.GetValue(PendingProperty)) return;
        viewer.SetValue(PendingProperty, true);
        viewer.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            viewer.SetValue(PendingProperty, false);
            if (!GetEnabled(viewer)) return;
            if (viewer.Content is not ItemsControl items) return;
            var viewport = new Rect(0, 0, viewer.ViewportWidth, viewer.ViewportHeight);
            for (var index = 0; index < items.Items.Count; index++)
            {
                if (items.Items[index] is not ScreenshotItem item) continue;
                var container = items.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                var visible = false;
                if (viewer.IsVisible && container is { IsVisible: true, IsArrangeValid: true })
                {
                    var bounds = container.TransformToAncestor(viewer).TransformBounds(new Rect(container.RenderSize));
                    visible = viewport.IntersectsWith(bounds);
                }
                item.SetVisible(visible);
            }
        });
    }

    private static void Release(ScrollViewer viewer)
    {
        if (viewer.Content is not ItemsControl items) return;
        foreach (var item in items.Items.OfType<ScreenshotItem>()) item.SetVisible(false);
    }
}
