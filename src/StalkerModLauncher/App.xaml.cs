using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Services;

namespace StalkerModLauncher;

public sealed partial class App : Application, IDisposable
{
    private AppServices? _services;
    private SingleInstanceGuard? _singleInstance;
    private readonly UiSoundService _uiSoundService = new();
    private TrayIconService? _trayIconService;
    private ViewModels.MainViewModel? _mainViewModel;
    private Views.MainWindow? _launcherWindow;
    private bool _startMinimized;
    private bool _isExiting;
    private bool _disposed;
    private AppServices Services => _services ?? throw new InvalidOperationException("Сервисы приложения не инициализированы.");
    private SingleInstanceGuard SingleInstance => _singleInstance ?? throw new InvalidOperationException("Контроль экземпляра не инициализирован.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (LauncherSelfUpdateService.TryParseRequest(e.Args, out var updateRequest))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ApplyLauncherUpdateAsync(updateRequest!);
            return;
        }

        LauncherSelfUpdateService.ScheduleUpdaterCleanup(e.Args);
        _services = new AppServices();
        _singleInstance = new SingleInstanceGuard("StalkerModLauncher");
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(ButtonBase_OnClick));
        EventManager.RegisterClassHandler(typeof(ListBox), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ListBox_OnSelectionChanged));

        if (!SingleInstance.IsPrimaryInstance)
        {
            MessageBox.Show(
                "Лаунчер уже запущен. Используйте открытое окно программы.",
                "CORDON",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _startMinimized = e.Args.Any(argument =>
            argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        try
        {
            Services.Paths.EnsureStorageWritable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Портативная папка недоступна для записи:\n{Services.Paths.ConfigDirectory}\n\n{ex.Message}",
                "Ошибка хранения данных", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        _uiSoundService.Initialize();

        if (_startMinimized)
        {
            _ = ShowLauncherSafelyAsync();
            return;
        }

        BitmapImage? bitmap = null;
        try
        {
            var asm = typeof(App).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ModLauncherLoadingLogo.png", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
            }
        }
        catch (Exception ex)
        {
            Services.ApplicationLogService.Write(
                $"Splash screen loading failed: {ex}",
                messageLevel: Models.LauncherLogLevel.ErrorsOnly);
        }

        if (bitmap is null)
        {
            _ = ShowLauncherSafelyAsync();
            return;
        }

        var width = Math.Min(bitmap.PixelWidth, 600);
        var height = Math.Min(bitmap.PixelHeight, 400);

        var splash = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Width = width,
            Height = height,
            Opacity = 0,
            Topmost = true,
            Content = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform
            },
            ShowInTaskbar = false
        };

        splash.Show();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
        fadeIn.Completed += (_, _) =>
        {
            var timer = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(1500));
            timer.Completed += async (_, _) =>
            {
                try
                {
                    await ShowLauncherSafelyAsync(() => splash.Close());
                }
                finally
                {
                    if (splash.IsVisible)
                    {
                        splash.Close();
                    }
                }
            };
            splash.BeginAnimation(UIElement.OpacityProperty, timer);
        };
        splash.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIconService?.Dispose();
        _uiSoundService.Dispose();
        _singleInstance?.Dispose();
        _services?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || button is RepeatButton)
        {
            return;
        }

        if (UiSound.GetKind(button) == UiSoundKind.ProfileActionsToggle && button is ToggleButton toggle)
        {
            _uiSoundService.Play(toggle.IsChecked == true
                ? UiSoundEffect.ProfileActionsOpened
                : UiSoundEffect.ProfileActionsClosed);
            return;
        }

        _uiSoundService.Play(UiSoundEffect.ButtonPress);
    }

    private void ListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { Name: "ProfilesList", IsLoaded: true } profileList ||
            e.AddedItems.Count == 0 ||
            (!profileList.IsMouseOver && !profileList.IsKeyboardFocusWithin))
        {
            return;
        }

        _uiSoundService.Play(UiSoundEffect.ButtonPress);
    }

    private Views.MainWindow CreateMainWindow()
    {
        _mainViewModel = Services.CreateMainViewModel();
        _mainViewModel.LauncherUpdateInstallationRequested += (_, _) => _ = ExitLauncherAsync();
        _launcherWindow = new Views.MainWindow(
            _mainViewModel,
            Services.WindowNavigationService);
        return _launcherWindow;
    }

    private async Task ShowLauncherAsync(Action? launcherShown = null)
    {
        var main = CreateMainWindow();
        MainWindow = main;
        var pdaIsActive = await main.ShowInitialInterfaceAsync(_startMinimized);
        _trayIconService = new TrayIconService(
            _mainViewModel!,
            main.ShowFromTray,
            () => _ = ExitLauncherAsync(),
            Services.ApplicationLogService);

        var remainsHidden = _startMinimized && _mainViewModel!.ShowTrayIcon;
        if (_startMinimized && !remainsHidden)
        {
            main.ShowFromTray();
        }

        launcherShown?.Invoke();
        if (!pdaIsActive && !remainsHidden)
        {
            await ShowAboutIfNeededAsync(main);
        }

        if (_mainViewModel!.AutoCheckForUpdates)
        {
            _ = CheckForUpdatesAtStartupAsync();
        }
    }

    private async Task ShowLauncherSafelyAsync(Action? launcherShown = null)
    {
        try
        {
            await ShowLauncherAsync(launcherShown);
        }
        catch (Exception ex)
        {
            Services.ApplicationLogService.Write(
                $"Launcher UI startup failed: {ex}",
                messageLevel: Models.LauncherLogLevel.ErrorsOnly);
            MessageBox.Show(
                $"Не удалось открыть окно лаунчера. Подробности записаны в журнал.\n\n{ex.Message}",
                "Ошибка запуска лаунчера",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task ShowAboutIfNeededAsync(Window? owner = null)
    {
        await Services.WindowNavigationService.ShowAboutAsync(owner, onlyIfNeeded: true);
    }

    private async Task CheckForUpdatesAtStartupAsync()
    {
        try
        {
            var result = await Services.LauncherUpdateService.CheckAsync();
            if (result.IsUpdateAvailable)
            {
                if (_mainViewModel?.ShowUpdateNotifications == true)
                {
                    _trayIconService?.ShowUpdateAvailable(result);
                }
                _mainViewModel?.AppendLog(
                    $"Launcher update available: {result.LatestVersion}.",
                    Models.LauncherLogLevel.Standard);
            }
        }
        catch (Exception ex)
        {
            _mainViewModel?.AppendLog(
                $"Automatic update check failed: {ex.Message}",
                Models.LauncherLogLevel.ErrorsOnly);
        }
    }

    private async Task ExitLauncherAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (_mainViewModel is not null)
        {
            await _mainViewModel.CleanupAsync();
        }

        _trayIconService?.Dispose();
        _trayIconService = null;
        _launcherWindow?.CloseAfterCleanup();
        Shutdown();
    }

    private async Task ApplyLauncherUpdateAsync(LauncherUpdateRequest request)
    {
        try
        {
            await LauncherSelfUpdateService.ApplyAsync(request);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось установить обновление.\n\n{ex.Message}",
                "Ошибка обновления CORDON",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Shutdown();
        }
    }
}
