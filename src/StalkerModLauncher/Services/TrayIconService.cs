using System.Drawing;
using System.ComponentModel;
using Forms = System.Windows.Forms;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.ViewModels;
using StalkerModLauncher.Views;

namespace StalkerModLauncher.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly TrayProfilePanel _panel;
    private readonly MainViewModel _viewModel;
    private readonly Action _openLauncher;
    private readonly Action _exitLauncher;
    private readonly ApplicationLogService _applicationLogService;
    private readonly Icon? _ownedIcon;
    private string? _releaseUrl;
    private bool _panelWasVisibleOnMouseDown;
    private bool _disposed;

    public TrayIconService(
        MainViewModel viewModel,
        Action openLauncher,
        Action exitLauncher,
        ApplicationLogService applicationLogService)
    {
        _openLauncher = openLauncher;
        _exitLauncher = exitLauncher;
        _applicationLogService = applicationLogService;
        _viewModel = viewModel;
        _panel = new TrayProfilePanel(viewModel, openLauncher);
        _ownedIcon = TryLoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "CORDON — S.T.A.L.K.E.R. Mod Launcher",
            Visible = viewModel.ShowTrayIcon,
            ContextMenuStrip = CreateContextMenu()
        };
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        _notifyIcon.MouseDown += NotifyIcon_OnMouseDown;
        _notifyIcon.MouseClick += NotifyIcon_OnMouseClick;
        _notifyIcon.BalloonTipClicked += NotifyIcon_OnBalloonTipClicked;
    }

    public void ShowUpdateAvailable(LauncherUpdateResult result)
    {
        _releaseUrl = result.ReleaseUrl;
        _notifyIcon.BalloonTipTitle = Strings.Tray_UpdateAvailable;
        _notifyIcon.BalloonTipText = LocalizedText.Format(Strings.Tray_UpdateReadyFormat, result.LatestVersion);
        _notifyIcon.ShowBalloonTip(5000);
    }

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Main_OpenLauncher, null, (_, _) => _openLauncher());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Tray_Exit, null, (_, _) => _exitLauncher());
        return menu;
    }

    private void NotifyIcon_OnMouseDown(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            _panelWasVisibleOnMouseDown = System.Windows.Application.Current.Dispatcher.Invoke(
                () => _panel.IsPanelOpen || _panel.WasRecentlyHidden);
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowTrayIcon))
        {
            return;
        }

        if (!_viewModel.ShowTrayIcon)
        {
            _panel.HidePanel();
        }

        _notifyIcon.Visible = _viewModel.ShowTrayIcon;
    }

    private void NotifyIcon_OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            var wasVisible = _panelWasVisibleOnMouseDown;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => TogglePanelSafely(wasVisible));
        }
    }

    private void TogglePanelSafely(bool wasVisible)
    {
        try
        {
            if (wasVisible)
            {
                _panel.HidePanel();
            }
            else
            {
                _panel.ShowNearTray();
            }
        }
        catch (Exception ex)
        {
            _applicationLogService.Write(
                LocalizedText.Format(Strings.Log_TrayPanelFailedFormat, ex),
                messageLevel: LauncherLogLevel.ErrorsOnly);
            System.Windows.MessageBox.Show(
                Strings.Tray_OpenFailed,
                Strings.Tray_ErrorTitle,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void NotifyIcon_OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl))
        {
            return;
        }

        try
        {
            DialogService.OpenUrl(_releaseUrl);
        }
        catch (Exception ex)
        {
            _applicationLogService.Write(
                LocalizedText.Format(Strings.Log_ReleasePageFailedFormat, ex),
                messageLevel: LauncherLogLevel.ErrorsOnly);
            System.Windows.MessageBox.Show(
                Strings.Tray_ReleaseOpenFailed,
                Strings.Tray_LinkErrorTitle,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private static Icon? TryLoadApplicationIcon()
    {
        try
        {
            return Environment.ProcessPath is { } path
                ? Icon.ExtractAssociatedIcon(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _panel.ClosePanel();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
