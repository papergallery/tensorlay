using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TensorLay.Models;
using TensorLay.Services;

namespace TensorLay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ProcessManager _processManager;
    private readonly HealthCheckService _healthCheckService;
    private readonly InstallerService _installerService;
    private readonly ModelDownloader _modelDownloader;
    private readonly SettingsService _settingsService;
    private readonly GpuMonitor _gpuMonitor;
    private readonly SshTunnelService _sshTunnelService;
    private readonly PairingService _pairingService;
    private readonly SshKeyService _sshKeyService;

    [ObservableProperty]
    private ObservableCollection<ServiceViewModel> _services = new();

    public string AppVersion => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    [ObservableProperty]
    private bool _isTunnelConnected;

    [ObservableProperty]
    private string _tunnelStatus = "Disconnected";

    [ObservableProperty]
    private GpuInfo? _gpuInfo;

    [ObservableProperty]
    private string _vramDisplay = "N/A";

    [ObservableProperty]
    private ObservableCollection<string> _logLines = new();

    [ObservableProperty]
    private string _selectedPage = "Dashboard";

    [ObservableProperty]
    private string _selectedLogService = "All";

    [ObservableProperty]
    private bool _isPaired;

    public int ActiveServicesCount =>
        Services.Count(s => s.State == ServiceState.Running);

    public int TotalModelsCount =>
        Services.Sum(s => s.Models.Count);

    public bool HasInstalledServices =>
        Services.Any(s => s.State != ServiceState.NotInstalled);

    public bool HasModels =>
        TotalModelsCount > 0;

    public void NotifyCountersChanged()
    {
        OnPropertyChanged(nameof(ActiveServicesCount));
        OnPropertyChanged(nameof(TotalModelsCount));
        OnPropertyChanged(nameof(HasInstalledServices));
        OnPropertyChanged(nameof(HasModels));
    }

    public MainViewModel(
        ProcessManager processManager,
        HealthCheckService healthCheckService,
        InstallerService installerService,
        ModelDownloader modelDownloader,
        SettingsService settingsService,
        GpuMonitor gpuMonitor,
        SshTunnelService sshTunnelService,
        PairingService pairingService,
        SshKeyService sshKeyService)
    {
        _processManager = processManager;
        _healthCheckService = healthCheckService;
        _installerService = installerService;
        _modelDownloader = modelDownloader;
        _settingsService = settingsService;
        _gpuMonitor = gpuMonitor;
        _sshTunnelService = sshTunnelService;
        _pairingService = pairingService;
        _sshKeyService = sshKeyService;

        IsPaired = settingsService.Load().IsPaired;
    }

    public void Initialize()
    {
        foreach (var definition in ServiceRegistry.GetAll())
        {
            var vm = new ServiceViewModel(
                definition,
                _processManager,
                _healthCheckService,
                _installerService,
                _modelDownloader,
                _settingsService);
            Services.Add(vm);
        }

        _gpuMonitor.GpuInfoUpdated += OnGpuInfoUpdated;
        _sshTunnelService.ConnectionStatusChanged += OnTunnelConnectionChanged;
        _sshTunnelService.TunnelLog += OnTunnelLog;
        _processManager.OutputReceived += OnProcessOutput;
        _installerService.InstallLog += OnInstallLog;
    }

    private void OnGpuInfoUpdated(GpuInfo info)
    {
        RunOnUI(() =>
        {
            GpuInfo = info;
            VramDisplay = info.VramTotalMb > 0
                ? $"{info.VramUsedMb} / {info.VramTotalMb} MB"
                : "N/A";
        });
    }

    private void OnTunnelConnectionChanged(bool isConnected)
    {
        RunOnUI(() =>
        {
            IsTunnelConnected = isConnected;
            TunnelStatus = isConnected ? "Connected" : "Disconnected";
            ConnectTunnelCommand.NotifyCanExecuteChanged();
            DisconnectTunnelCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnTunnelLog(string message)
    {
        RunOnUI(() => AddLog($"[Tunnel] {message}"));
    }

    private void OnProcessOutput(string serviceId, string line)
    {
        RunOnUI(() => AddLog($"[{serviceId}] {line}"));
    }

    private void OnInstallLog(string serviceId, string line)
    {
        RunOnUI(() => AddLog($"[install:{serviceId}] {line}"));
    }

    private void AddLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        // keep last 500 lines to avoid unbounded growth
        while (LogLines.Count > 500)
            LogLines.RemoveAt(0);
    }

    private bool CanConnectTunnel() => !IsTunnelConnected;
    private bool CanDisconnectTunnel() => IsTunnelConnected;

    [RelayCommand(CanExecute = nameof(CanConnectTunnel))]
    private async Task ConnectTunnel()
    {
        var settings = _settingsService.Load();

        // First connection — open pairing window
        if (!settings.IsPaired)
        {
            var window = new Views.PairingWindow(_pairingService, _sshKeyService, _settingsService);
            window.Owner = System.Windows.Application.Current.MainWindow;
            if (window.ShowDialog() != true)
                return;

            IsPaired = true;
            settings = _settingsService.Load(); // reload after pairing saved settings
        }

        await _sshTunnelService.Connect(settings, GetCurrentlyTunnelablePorts);
    }

    /// <summary>
    /// Returns ports for every installed service, regardless of whether it is
    /// currently Running, Starting, Stopped, Stopping, or in Error.
    /// Forwarding an unused port is harmless; failing to forward a port the
    /// user is about to use is the bug. Only NotInstalled / Installing are
    /// excluded since they have no usable backend yet.
    /// This delegate is re-evaluated on every (re)connect attempt by
    /// SshTunnelService so that services installed/started after the initial
    /// connect get picked up.
    /// </summary>
    private IReadOnlyList<int> GetCurrentlyTunnelablePorts()
    {
        return Services
            .Where(s => s.State != ServiceState.NotInstalled
                     && s.State != ServiceState.Installing)
            .Select(s => s.Definition.Port)
            .ToList();
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectTunnel))]
    private async Task DisconnectTunnel()
    {
        await _sshTunnelService.Disconnect();
    }

    [RelayCommand]
    private async Task StartAll()
    {
        var tasks = Services
            .Where(s => s.State == ServiceState.Stopped)
            .Select(s => s.StartCommand.ExecuteAsync(null));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private async Task StopAll()
    {
        var tasks = Services
            .Where(s => s.State == ServiceState.Running || s.State == ServiceState.Starting)
            .Select(s => s.StopCommand.ExecuteAsync(null));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void NavigateTo(string page)
    {
        SelectedPage = page;
    }

    partial void OnServicesChanged(ObservableCollection<ServiceViewModel> value)
    {
        OnPropertyChanged(nameof(ActiveServicesCount));
        OnPropertyChanged(nameof(TotalModelsCount));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settingsService);
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenPairing()
    {
        var window = new Views.PairingWindow(_pairingService, _sshKeyService, _settingsService);
        window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.ShowDialog() == true)
        {
            IsPaired = true;
            // Auto-connect tunnel after successful pairing
            await ConnectTunnel();
        }
    }
}
