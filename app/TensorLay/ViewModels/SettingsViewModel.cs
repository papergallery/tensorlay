using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TensorLay.Models;
using TensorLay.Services;
using Microsoft.Win32;
using Renci.SshNet;
using WinForms = System.Windows.Forms;

namespace TensorLay.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private string _vpsHost = "";

    [ObservableProperty]
    private string _vpsUser = "";

    [ObservableProperty]
    private string _sshKeyPath = "";

    [ObservableProperty]
    private string _installDirectory = "";

    [ObservableProperty]
    private int _sshPort = 22;

    [ObservableProperty]
    private bool _autostartWithWindows;

    [ObservableProperty]
    private bool _autoconnectTunnel;

    [ObservableProperty]
    private bool _allowRemoteInstallRequests;

    [ObservableProperty]
    private bool _allowRemoteLogRequests;

    [ObservableProperty]
    private bool _allowRemoteModelCleanup;

    [ObservableProperty]
    private bool _allowRemoteCommandBridge;

    // The bridge bearer token, shown so the user can copy it to the VPS
    // (~/.tensorlay_bridge_token). Generated on first enable + Save.
    [ObservableProperty]
    private string _remoteCommandBridgeToken = "";

    [ObservableProperty]
    private int _remoteCommandBridgePort = 6520;

    // True only when the relay returned a non-empty remote_tasks_token at
    // pair time (i.e. relay >= v1.3.0). Toggle is grayed out when false.
    [ObservableProperty]
    private bool _remoteInstallSupported;

    [ObservableProperty]
    private string _connectionTestResult = "";

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Load();
    }

    public void Load()
    {
        var settings = _settingsService.Load();
        VpsHost = settings.VpsHost;
        VpsUser = settings.VpsUser;
        SshKeyPath = settings.SshKeyPath;
        InstallDirectory = settings.InstallDirectory;
        SshPort = settings.SshPort;
        AutostartWithWindows = settings.AutostartWithWindows;
        AutoconnectTunnel = settings.AutoconnectTunnel;
        AllowRemoteInstallRequests = settings.AllowRemoteInstallRequests;
        AllowRemoteLogRequests = settings.AllowRemoteLogRequests;
        AllowRemoteModelCleanup = settings.AllowRemoteModelCleanup;
        AllowRemoteCommandBridge = settings.AllowRemoteCommandBridge;
        RemoteCommandBridgeToken = settings.RemoteCommandBridgeToken;
        RemoteCommandBridgePort = settings.RemoteCommandBridgePort;
        RemoteInstallSupported = !string.IsNullOrEmpty(settings.RemoteTasksToken);
    }

    /// <summary>
    /// Re-read only the fields /pair rewrites (host, user, key path, port,
    /// autoconnect, relay capability). Called after a successful Re-pair…
    /// from this window; leaves the user's unsaved edits to the other
    /// fields alone.
    /// </summary>
    public void ReloadAfterPairing()
    {
        var settings = _settingsService.Load();
        VpsHost = settings.VpsHost;
        VpsUser = settings.VpsUser;
        SshKeyPath = settings.SshKeyPath;
        SshPort = settings.SshPort;
        AutoconnectTunnel = settings.AutoconnectTunnel;
        RemoteInstallSupported = !string.IsNullOrEmpty(settings.RemoteTasksToken);
        ConnectionTestResult = "";
    }

    [RelayCommand]
    private void Save()
    {
        // Mutate the existing settings instance — constructing a fresh
        // AppSettings here would silently wipe IsPaired, SshHostKeyFingerprints,
        // RemoteTasksToken, RejectedAgentLabels, etc. Pre-0.9.0 the Save()
        // path was unintentionally clobbering pairing state on every click;
        // keep all unrelated fields intact.
        var settings = _settingsService.Load();
        settings.VpsHost = VpsHost;
        settings.VpsUser = VpsUser;
        settings.SshKeyPath = SshKeyPath;
        settings.InstallDirectory = InstallDirectory;
        settings.SshPort = SshPort;
        settings.AutostartWithWindows = AutostartWithWindows;
        settings.AutoconnectTunnel = AutoconnectTunnel;
        settings.AllowRemoteInstallRequests = AllowRemoteInstallRequests;
        settings.AllowRemoteLogRequests = AllowRemoteLogRequests;
        settings.AllowRemoteModelCleanup = AllowRemoteModelCleanup;

        // Bridge: issue a token the first time the feature is turned on so the
        // listener has something to check against. We never clear the token on
        // disable — turning the bridge back on later keeps the same secret the
        // VPS already has. Use Regenerate to deliberately rotate it.
        settings.AllowRemoteCommandBridge = AllowRemoteCommandBridge;
        if (AllowRemoteCommandBridge && string.IsNullOrEmpty(RemoteCommandBridgeToken))
            RemoteCommandBridgeToken = RemoteBridgeService.GenerateToken();
        settings.RemoteCommandBridgeToken = RemoteCommandBridgeToken;
        settings.RemoteCommandBridgePort = RemoteCommandBridgePort;

        _settingsService.Save(settings);
    }

    [RelayCommand]
    private void CopyBridgeToken()
    {
        if (!string.IsNullOrEmpty(RemoteCommandBridgeToken))
        {
            try { System.Windows.Clipboard.SetText(RemoteCommandBridgeToken); }
            catch { /* clipboard busy — user can select the text manually */ }
        }
    }

    // Rotate the token in the UI only; it persists on the next Save. The old
    // token keeps working until then, so the VPS side has time to update.
    [RelayCommand]
    private void RegenerateBridgeToken()
    {
        RemoteCommandBridgeToken = RemoteBridgeService.GenerateToken();
    }

    [RelayCommand]
    private void BrowseSshKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH private key",
            Filter = "All files (*.*)|*.*|PEM files (*.pem)|*.pem|PPK files (*.ppk)|*.ppk",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            SshKeyPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseInstallDir()
    {
        // WPF does not have a built-in folder browser dialog in .NET 8
        // Using OpenFileDialog in directory mode via Win32 FolderBrowserDialog
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select installation directory",
            SelectedPath = InstallDirectory,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            InstallDirectory = dialog.SelectedPath;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        ConnectionTestResult = "Testing...";

        try
        {
            await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(SshKeyPath) || !File.Exists(SshKeyPath))
                    throw new FileNotFoundException($"SSH key not found: {SshKeyPath}");

                var keyFile = new PrivateKeyFile(SshKeyPath);
                using var client = new SshClient(VpsHost, SshPort, VpsUser, keyFile);
                client.Connect();
                client.Disconnect();
            });

            ConnectionTestResult = "Connection successful!";
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Failed: {ex.Message}";
        }
    }
}
