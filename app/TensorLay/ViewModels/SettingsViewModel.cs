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
        RemoteInstallSupported = !string.IsNullOrEmpty(settings.RemoteTasksToken);
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
        _settingsService.Save(settings);
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
