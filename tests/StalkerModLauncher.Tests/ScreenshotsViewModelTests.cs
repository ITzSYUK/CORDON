using System.Windows.Media;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ScreenshotsViewModelTests
{
    [Fact]
    public void MissingScreenshotDoesNotReplaceCurrentSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "CordonScreenshotTest", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var first = Path.Combine(directory, "first.png");
                var second = Path.Combine(directory, "second.png");
                var third = Path.Combine(directory, "third.png");
                const int width = 1920;
                const int height = 1080;
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
                foreach (var path in new[] { first, second, third })
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }

                using var viewModel = new ScreenshotsViewModel(new ModProfile(), new ScreenshotClipboardService());
                viewModel.Screenshots.Add(new ScreenshotItem(first));
                viewModel.Screenshots.Add(new ScreenshotItem(second));
                var backgroundItem = Task.Run(() => ScreenshotImageLoader.LoadThumbnail(first)).GetAwaiter().GetResult();
                Assert.True(backgroundItem.IsFrozen);
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                var lazyItem = new ScreenshotItem(first);
                Assert.Null(lazyItem.Thumbnail);
                lazyItem.SetVisible(true);
                PumpDispatcher();
                Assert.NotNull(lazyItem.Thumbnail);
                lazyItem.SetVisible(false);
                Assert.Null(lazyItem.Thumbnail);
                lazyItem.SetVisible(true);
                lazyItem.SetVisible(false);
                PumpDispatcher();
                Assert.Null(lazyItem.Thumbnail);
                var items = new System.Windows.Controls.ItemsControl();
                var style = new System.Windows.Style(typeof(System.Windows.Controls.ContentPresenter));
                style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.WidthProperty, 100d));
                style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.HeightProperty, 100d));
                items.ItemContainerStyle = style;
                for (var i = 0; i < 40; i++) items.Items.Add(new ScreenshotItem(first));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var baseline = Enumerable.Range(0, 40).Select(_ => ScreenshotImageLoader.LoadThumbnail(first)).ToArray();
                watch.Stop();
                var baselineMs = watch.ElapsedMilliseconds;
                Assert.Equal(40, baseline.Length);
                GC.KeepAlive(baseline);
                var viewer = new System.Windows.Controls.ScrollViewer { Content = items, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
                StalkerModLauncher.Infrastructure.ScreenshotViewportBehavior.SetEnabled(viewer, true);
                var window = new System.Windows.Window { Content = viewer, Width = 250, Height = 200, ShowInTaskbar = false, Opacity = 0 };
                try
                {
                    window.Show();
                    watch.Restart();
                    PumpDispatcher();
                    watch.Stop();
                    Console.WriteLine($"Gallery fixture 40 x 1920x1080: eager {baselineMs} ms, viewport {watch.ElapsedMilliseconds} ms, loaded {items.Items.Cast<ScreenshotItem>().Count(item => item.Thumbnail is not null)}");
                    Assert.NotNull(((ScreenshotItem)items.Items[0]).Thumbnail);
                    Assert.Null(((ScreenshotItem)items.Items[39]).Thumbnail);
                    viewer.ScrollToBottom();
                    PumpDispatcher();
                    Assert.Null(((ScreenshotItem)items.Items[0]).Thumbnail);
                    Assert.NotNull(((ScreenshotItem)items.Items[39]).Thumbnail);
                    viewer.Visibility = System.Windows.Visibility.Collapsed;
                    PumpDispatcher();
                    Assert.Null(((ScreenshotItem)items.Items[39]).Thumbnail);
                    viewer.Visibility = System.Windows.Visibility.Visible;
                    PumpDispatcher();
                    Assert.NotNull(((ScreenshotItem)items.Items[39]).Thumbnail);
                }
                finally { window.Close(); }
                viewModel.OpenScreenshot(0);
                var selected = viewModel.SelectedScreenshot;
                File.Delete(second);
                viewModel.GoNext();
                Assert.Same(selected, viewModel.SelectedScreenshot);
                Assert.True(viewModel.CanGoNext);
                Assert.NotEmpty(viewModel.StatusText);
                viewModel.Screenshots.Add(new ScreenshotItem(third));
                viewModel.GoNext();
                Assert.NotSame(selected, viewModel.SelectedScreenshot);
                Assert.False(viewModel.CanGoNext);
                viewModel.GoPrevious();
                Assert.True(viewModel.CanGoNext);
            }
            catch (Exception ex) { failure = ex; }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
