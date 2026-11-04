using System.Windows;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Views.Controls;

public partial class StorageMigrationView : System.Windows.Controls.UserControl, IDisposable
{
    private readonly Func<IProgress<(double Fraction, string Path)>, Task>? _transfer;
    private bool _isTransferring;
    private bool _finished;
    private readonly DialogService _dialogs;

    public StorageMigrationView(string destination, string sources, Func<IProgress<(double Fraction, string Path)>, Task>? transfer = null, DialogService? dialogs = null)
    {
        InitializeComponent();
        DestinationText.Text = destination;
        SourcesText.Text = sources;
        _transfer = transfer;
        _dialogs = dialogs ?? new DialogService();

    }

    public bool IsTransferring => _isTransferring;
    public event EventHandler<bool>? Completed;

    private async void ProfilesOnly_OnClick(object sender, RoutedEventArgs e) => await TransferAsync();

    private async Task TransferAsync()
    {
        if (_isTransferring || _finished) return;
        _isTransferring = true;
        ProfilesOnlyButton.IsEnabled = false;
        DeclineButton.IsEnabled = false;
        MigrationProgressBar.Value = 0;
        MigrationStatus.Text = Strings.Migration_Preparing;
        MigrationStatus.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<(double Fraction, string Path)>(value =>
            {
                if (!_isTransferring) return;
                MigrationProgressBar.Value = Math.Clamp(value.Fraction, MigrationProgressBar.Value, 0.99);
                MigrationStatus.Text = LocalizedText.Format(Strings.Migration_CopyingFormat, value.Path);
            });
            if (_transfer is not null) await _transfer(progress);
            MigrationProgressBar.IsIndeterminate = false;
            MigrationProgressBar.Value = 1;
            _isTransferring = false;
            Complete(true);
        }
        catch (Exception ex)
        {
            MigrationProgressBar.IsIndeterminate = false;
            _dialogs.ShowError(Strings.App_StorageErrorTitle, LocalizedText.Format(Strings.Migration_FailedFormat, ex.Message));
            _isTransferring = false;
            ProfilesOnlyButton.IsEnabled = true;
            DeclineButton.IsEnabled = true;
        }
    }

    private void View_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || _isTransferring) return;
        e.Handled = true;
        Complete(false);
    }

    private void Complete(bool accepted)
    {
        if (_finished || _isTransferring) return;
        _finished = true;
        Completed?.Invoke(this, accepted);
    }

    public void Dispose()
    {
        Complete(false);
        GC.SuppressFinalize(this);
    }

    private void Decline_OnClick(object sender, RoutedEventArgs e) => Complete(false);
}
