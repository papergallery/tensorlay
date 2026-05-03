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
    private readonly RemoteTaskService _remoteTaskService;
    // FIFO queue for tasks that arrive while another approval modal is open.
    // Drained one-at-a-time on each DecisionMade event, so a burst of agent
    // submissions doesn't pile up overlapping windows.
    private readonly Queue<Models.RemoteTask> _pendingRemoteTasks = new();
    private bool _isApprovalModalOpen;

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

    // Number of pending remote install requests in the queue (badge in nav).
    // Updated when RemoteTaskService surfaces a task and when the approval
    // modal's DecisionMade dequeues one.
    [ObservableProperty]
    private int _pendingRemoteTaskCount;

    // True when the relay supports remote tasks (i.e. /pair returned a
    // non-empty remote_tasks_token). Drives whether the Settings toggle is
    // interactive vs grayed-out with "update your relay" tooltip.
    [ObservableProperty]
    private bool _remoteTasksSupported;

    public int ActiveServicesCount =>
        Services.Count(s => s.State == ServiceState.Running);

    public int TotalModelsCount =>
        Services.Sum(s => s.Models.Count);

    public bool HasInstalledServices =>
        Services.Any(s => s.State != ServiceState.NotInstalled);

    public bool HasModels =>
        TotalModelsCount > 0;

    public bool HasPendingRemoteTasks => PendingRemoteTaskCount > 0;

    partial void OnPendingRemoteTaskCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingRemoteTasks));
    }

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
        SshKeyService sshKeyService,
        RemoteTaskService remoteTaskService)
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
        _remoteTaskService = remoteTaskService;

        var s = settingsService.Load();
        IsPaired = s.IsPaired;
        RemoteTasksSupported = !string.IsNullOrEmpty(s.RemoteTasksToken);
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

        _remoteTaskService.TaskReceived += OnRemoteTaskReceived;
        _remoteTaskService.RemoteTaskLog += msg => RunOnUI(() => AddLog($"[remote] {msg}"));

        // Start polling only if pairing + supported relay + user opt-in all
        // line up. Settings toggle changes call StartRemotePolling/StopRemotePolling
        // explicitly so the loop reflects the current state without a relaunch.
        var settings = _settingsService.Load();
        if (settings.IsPaired && settings.AllowRemoteInstallRequests
            && !string.IsNullOrEmpty(settings.RemoteTasksToken))
        {
            _remoteTaskService.StartPolling();
        }
    }

    public void StartRemotePolling() => _remoteTaskService.StartPolling();
    public Task StopRemotePolling() => _remoteTaskService.StopPolling();

    private void OnRemoteTaskReceived(Models.RemoteTask task)
    {
        RunOnUI(() =>
        {
            _pendingRemoteTasks.Enqueue(task);
            PendingRemoteTaskCount = _pendingRemoteTasks.Count + (_isApprovalModalOpen ? 1 : 0);
            ShowNextRemoteTaskIfIdle();
        });
    }

    private void ShowNextRemoteTaskIfIdle()
    {
        if (_isApprovalModalOpen) return;
        if (_pendingRemoteTasks.Count == 0) return;

        var task = _pendingRemoteTasks.Dequeue();
        PendingRemoteTaskCount = _pendingRemoteTasks.Count + 1; // include the one we're showing
        _isApprovalModalOpen = true;

        var vm = new RemoteInstallViewModel(_remoteTaskService, _settingsService, task);
        var window = new Views.RemoteInstallApprovalWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.Closed += (_, _) =>
        {
            _isApprovalModalOpen = false;
            PendingRemoteTaskCount = _pendingRemoteTasks.Count;
            ShowNextRemoteTaskIfIdle();
        };
        window.Show();   // non-modal so other UI stays responsive while a download runs
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
    private async Task OpenSettings()
    {
        bool wasPolling = _remoteTaskService.IsRunning;
        var window = new Views.SettingsWindow(_settingsService);
        if (window.ShowDialog() != true) return;

        // Settings was saved (Save button, not Cancel) — reconcile the
        // polling loop with the new flag state. Toggle ON → start; OFF →
        // stop; unchanged → no-op.
        var settings = _settingsService.Load();
        bool shouldPoll = settings.IsPaired && settings.AllowRemoteInstallRequests
                          && !string.IsNullOrEmpty(settings.RemoteTasksToken);
        if (shouldPoll && !wasPolling)
            _remoteTaskService.StartPolling();
        else if (!shouldPoll && wasPolling)
            await _remoteTaskService.StopPolling().ConfigureAwait(true);
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
