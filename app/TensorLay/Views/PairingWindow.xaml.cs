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
        SetStatus("Checking relay...", false);

        try
        {
            var healthy = await _pairingService.CheckRelayHealthAsync(vpsIp, ct);
            if (!healthy)
            {
                SetStatus("Cannot reach relay on port 8090. Is it installed?", true);
                ResetButtons();
                return;
            }

            SetStatus("Generating SSH key...", false);
            string publicKey;
            string keyPath;
            try
            {
                // RSA-4096 fallback can take seconds — keep it off the UI thread.
                keyPath = await _sshKeyService.GetOrCreateKeyPathAsync();
                publicKey = await _sshKeyService.GetPublicKeyAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Key error: {ex.Message}", true);
                ResetButtons();
                return;
            }

            SetStatus("Pairing...", false);
            var result = await _pairingService.PairAsync(vpsIp, code, publicKey, ct);

            if (result.Success)
            {
                var settings = _settingsService.Load();
                settings.VpsHost = vpsIp;
                settings.VpsUser = result.SshUser;
                settings.SshPort = result.SshPort;
                settings.SshKeyPath = keyPath;
                settings.IsPaired = true;
                settings.AutoconnectTunnel = true;
                // Pin the VPS's SSH host keys so any future Connect that
                // negotiates a different key (= different server) is rejected.
                settings.SshHostKeyFingerprints = result.HostKeyFingerprints ?? new List<string>();
                // v0.9.0 — store the rotated remote-tasks bearer. Empty when
                // the relay is older than 1.3.0; the polling loop checks for
                // this and stays idle until the user repairs against a newer
                // relay. We don't auto-enable AllowRemoteInstallRequests —
                // user has to flip it in Settings explicitly.
                settings.RemoteTasksToken = result.RemoteTasksToken ?? "";
                _settingsService.Save(settings);

                SetStatus("Paired successfully!", false);
                DialogResult = true;
                Close();
            }
            else
            {
                SetStatus(result.Error, true);
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
