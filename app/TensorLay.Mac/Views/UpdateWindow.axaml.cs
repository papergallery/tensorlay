using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TensorLay.Services;

namespace TensorLay.Mac.Views;

// Closes with true when the update was downloaded, verified and handed to
// the swap script — the caller then quits so the script can replace the app.
public partial class UpdateWindow : Window
{
    private readonly AutoUpdater? _updater;
    private bool _isDownloading;
    private bool _failed;

    public UpdateWindow() => InitializeComponent();

    public UpdateWindow(AutoUpdater updater, string newVersion) : this()
    {
        _updater = updater;
        VersionText.Text = $"v{updater.CurrentVersion}  →  v{newVersion}";
        _updater.DownloadProgress += OnProgress;
    }

    private void OnProgress(double percent) => Dispatcher.UIThread.Post(() =>
    {
        DownloadBar.Value = percent;
        ProgressPercent.Text = $"{percent:F0}%";
    });

    private async void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_updater is null || _isDownloading) return;
        if (_failed) { Close(false); return; }

        _isDownloading = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Downloading...";
        SkipButton.IsEnabled = false;
        ProgressPanel.IsVisible = true;
        TitleText.Text = "Updating...";

        if (await _updater.DownloadAndApply())
        {
            ProgressStatus.Text = "Restarting...";
            Close(true);
            return;
        }

        _failed = true;
        TitleText.Text = "Update failed";
        ProgressStatus.Text = _updater.LastError ?? "Unknown error";
        ProgressStatus.Foreground = new SolidColorBrush(Color.Parse("#E74C3C"));
        UpdateButton.Content = "Close";
        UpdateButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
        _isDownloading = false;
    }

    private void OnSkipClicked(object? sender, RoutedEventArgs e)
    {
        if (!_isDownloading) Close(false);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_updater is not null) _updater.DownloadProgress -= OnProgress;
        base.OnClosed(e);
    }
}
