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
        PairBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        PairBtnText.Text = "Connecting...";
        SetStatus("Checking relay...", false);

        var healthy = await _pairingService.CheckRelayHealthAsync(vpsIp);
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
            keyPath = _sshKeyService.GetOrCreateKeyPath();
            publicKey = _sshKeyService.GetPublicKey();
        }
        catch (Exception ex)
        {
            SetStatus($"Key error: {ex.Message}", true);
            ResetButtons();
            return;
        }

        SetStatus("Pairing...", false);
        var result = await _pairingService.PairAsync(vpsIp, code, publicKey);

        if (result.Success)
        {
            var settings = _settingsService.Load();
            settings.VpsHost = vpsIp;
            settings.VpsUser = result.SshUser;
            settings.SshPort = result.SshPort;
            settings.SshKeyPath = keyPath;
            settings.IsPaired = true;
            settings.AutoconnectTunnel = true;
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

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (_isPairing) return;
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
