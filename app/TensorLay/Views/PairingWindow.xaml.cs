using System.Threading;
using System.Windows;
using System.Windows.Media;
using TensorLay.Services;

namespace TensorLay.Views;

public partial class PairingWindow : Window
{
    private readonly PairingService _pairingService;
    private readonly SshKeyService _sshKeyService;
    private readonly SettingsService _settingsService;
    private bool _isPairing;
    private CancellationTokenSource? _pairingCts;

    public PairingWindow(PairingService pairingService, SshKeyService sshKeyService, SettingsService settingsService)
    {
        InitializeComponent();
        _pairingService = pairingService;
        _sshKeyService = sshKeyService;
        _settingsService = settingsService;

        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.VpsHost))
            VpsIpInput.Text = settings.VpsHost;
    }

    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(InstallCmd.Text);
        CopyBtnText.Text = "Copied!";
    }

    private async void OnPairClicked(object sender, RoutedEventArgs e)
    {
        if (_isPairing) return;

        var vpsIp = VpsIpInput.Text.Trim();
        var code = PairingCodeInput.Text.Trim().ToUpper();

        if (string.IsNullOrWhiteSpace(vpsIp))
        {
            SetStatus("Enter VPS IP address", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus("Enter pairing code from VPS", true);
            return;
        }

        _isPairing = true;
        _pairingCts?.Dispose();
        _pairingCts = new CancellationTokenSource();
        var ct = _pairingCts.Token;

        PairBtn.IsEnabled = false;
        // Keep CancelBtn enabled so the user can abort a hung request.
        CancelBtn.IsEnabled = true;
        PairBtnText.Text = "Connecting...";

        try
        {
            if (await PairingFlow.RunAsync(_pairingService, _sshKeyService, _settingsService,
                    vpsIp, code, SetStatus, ct))
            {
                DialogResult = true;
                Close();
            }
            else
            {
                ResetButtons();
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled in-flight pairing — reset state silently, no error popup.
            SetStatus("Cancelled.", false);
            ResetButtons();
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (_isPairing)
        {
            // Abort in-flight pairing instead of ignoring the click.
            _pairingCts?.Cancel();
            return;
        }
        DialogResult = false;
        Close();
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(
            isError ? Color.FromRgb(0xE7, 0x4C, 0x3C) : Color.FromRgb(0x9B, 0x9B, 0x9B));
    }

    private void ResetButtons()
    {
        _isPairing = false;
        PairBtn.IsEnabled = true;
        CancelBtn.IsEnabled = true;
        PairBtnText.Text = "Connect";
    }
}
