using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TensorLay.Services;

namespace TensorLay.Mac.Views;

// Collects host + code and runs the shared PairingFlow (same steps and the
// same saved fields as the Windows dialog). Closes with true on success.
public partial class PairingWindow : Window
{
    private readonly PairingService _pairingService;
    private readonly SshKeyService _sshKeyService;
    private readonly SettingsService _settingsService;
    private bool _isPairing;
    private CancellationTokenSource? _pairingCts;

    // Designer / XAML loader.
    public PairingWindow() : this(new PairingService(), new SshKeyService(), new SettingsService()) { }

    public PairingWindow(PairingService pairingService, SshKeyService sshKeyService, SettingsService settingsService)
    {
        InitializeComponent();
        _pairingService = pairingService;
        _sshKeyService = sshKeyService;
        _settingsService = settingsService;

        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.VpsHost))
            VpsIpInput.Text = settings.VpsHost;
        PairingCodeInput.TextChanged += (_, _) =>
        {
            // Codes are upper-case; mirror WPF's CharacterCasing="Upper".
            string t = PairingCodeInput.Text ?? "";
            if (t != t.ToUpperInvariant())
            {
                int caret = PairingCodeInput.CaretIndex;
                PairingCodeInput.Text = t.ToUpperInvariant();
                PairingCodeInput.CaretIndex = caret;
            }
        };
    }

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(InstallCmd.Text ?? "");
        CopyBtn.Content = "Copied!";
    }

    private async void OnPairClicked(object? sender, RoutedEventArgs e)
    {
        if (_isPairing) return;

        var vpsIp = (VpsIpInput.Text ?? "").Trim();
        var code = (PairingCodeInput.Text ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(vpsIp)) { SetStatus("Enter VPS IP address", true); return; }
        if (string.IsNullOrWhiteSpace(code)) { SetStatus("Enter pairing code from VPS", true); return; }

        _isPairing = true;
        _pairingCts?.Dispose();
        _pairingCts = new CancellationTokenSource();
        PairBtn.IsEnabled = false;
        PairBtn.Content = "Connecting...";

        try
        {
            if (await PairingFlow.RunAsync(_pairingService, _sshKeyService, _settingsService,
                    vpsIp, code, SetStatus, _pairingCts.Token))
            {
                Close(true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", false);
        }
        ResetButtons();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (_isPairing)
        {
            _pairingCts?.Cancel();
            return;
        }
        Close(false);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(isError ? "#E74C3C" : "#9B9B9B"));
    }

    private void ResetButtons()
    {
        _isPairing = false;
        PairBtn.IsEnabled = true;
        PairBtn.Content = "Connect";
    }
}
