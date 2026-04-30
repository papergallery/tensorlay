using System.Windows;
using TensorLay.Services;

namespace TensorLay.Views;

public partial class UpdateWindow : Window
{
    private readonly AutoUpdater _updater;
    private bool _isDownloading;

    public bool UserAccepted { get; private set; }

    public UpdateWindow(AutoUpdater updater, string newVersion)
    {
        InitializeComponent();

        _updater = updater;

        VersionText.Text = $"v{updater.CurrentVersion}  \u2192  v{newVersion}";

        _updater.DownloadProgress += OnProgress;
    }

    private void OnProgress(double percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadBar.Value = percent;
            ProgressPercent.Text = $"{percent:F0}%";
        });
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _isDownloading = true;

        UpdateButton.IsEnabled = false;
        UpdateButtonText.Text = "Downloading...";
        SkipButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        TitleText.Text = "Updating...";

        var success = await _updater.DownloadAndApply();

        if (success)
        {
            UserAccepted = true;
            ProgressStatus.Text = "Restarting...";
            DialogResult = true;
            Close();
        }
        else
        {
            TitleText.Text = "Update failed";
            ProgressStatus.Text = _updater.LastError ?? "Unknown error";
            ProgressStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE7, 0x4C, 0x3C));
            UpdateButtonText.Text = "Close";
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            _isDownloading = false;
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        UserAccepted = false;
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _updater.DownloadProgress -= OnProgress;
        base.OnClosed(e);
    }
}
