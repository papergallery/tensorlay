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
    private readonly RemoteCleanupService _remoteCleanupService;
    private readonly ModelDeletionService _modelDeletionService;
    // Attached after construction (App builds it with delegates that call
    // back into this VM — see AttachBridge). Null until then.
    private RemoteBridgeService? _remoteBridgeService;
    // One deletion window at a time — same pattern as remote install
    // approvals. Agent requests queue while a window is open; local Delete
    // clicks share the slot so the two kinds never stack on top of each other.
    private readonly Queue<RemoteDeletionRequest> _pendingDeletions = new();
    private Views.ModelDeletionWindow? _openDeletionWindow;
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

    // True while the command bridge is listening. Drives the warning banner
    // in the main window ("agent can run commands on this PC") and its
    // one-click Stop button. Kept in sync from the bridge's Log/lifecycle.
    [ObservableProperty]
    private bool _isBridgeActive;

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
        RemoteTaskService remoteTaskService,
        RemoteCleanupService remoteCleanupService,
        ModelDeletionService modelDeletionService)
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
        _remoteCleanupService = remoteCleanupService;
        _modelDeletionService = modelDeletionService;

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
            vm.DeleteModelRequested += (svc, model) => RequestModelDeletion(model);
            Services.Add(vm);
            // Populate the Models page at startup — before v0.9.27 the list
            // only filled after a download finished in the current session.
            if (vm.State != ServiceState.NotInstalled)
                vm.RefreshModels();
        }

        _gpuMonitor.GpuInfoUpdated += OnGpuInfoUpdated;
        _sshTunnelService.ConnectionStatusChanged += OnTunnelConnectionChanged;
        _sshTunnelService.TunnelLog += OnTunnelLog;
        _processManager.OutputReceived += OnProcessOutput;
        _installerService.InstallLog += OnInstallLog;

        _remoteTaskService.TaskReceived += OnRemoteTaskReceived;
        _remoteTaskService.RemoteTaskLog += msg => RunOnUI(() => AddLog($"[remote] {msg}"));

        _remoteCleanupService.DeletionReceived += OnDeletionReceived;
        _remoteCleanupService.Log += msg => RunOnUI(() => AddLog($"[cleanup] {msg}"));

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

    private void OnDeletionReceived(RemoteDeletionRequest request)
    {
        RunOnUI(() =>
        {
            _pendingDeletions.Enqueue(request);
            ShowNextDeletionIfIdle();
        });
    }

    private void ShowNextDeletionIfIdle()
    {
        if (_openDeletionWindow is not null || _pendingDeletions.Count == 0) return;

        var request = _pendingDeletions.Dequeue();

        var vm = ModelDeletionViewModel.ForRemoteRequest(_modelDeletionService, _remoteCleanupService, request);
        vm.Finished += OnDeletionsApplied;
        var window = new Views.ModelDeletionWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.Closed += (_, _) =>
        {
            vm.Finished -= OnDeletionsApplied;
            _openDeletionWindow = null;
            ShowNextDeletionIfIdle();
        };
        _openDeletionWindow = window;
        window.Show();   // non-modal, like install approvals
    }

    // Delete button on the Models page / service card: same window and code
    // path as a remote request, with a single pre-selected item.
    public void RequestModelDeletion(ModelInfo model)
    {
        // A deletion window is already up (usually an agent request): finish
        // that one first.
        if (_openDeletionWindow is not null)
        {
            _openDeletionWindow.Activate();
            return;
        }

        var vm = ModelDeletionViewModel.ForLocalModel(_modelDeletionService, model);
        vm.Finished += OnDeletionsApplied;
        var window = new Views.ModelDeletionWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        _openDeletionWindow = window;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            vm.Finished -= OnDeletionsApplied;
            _openDeletionWindow = null;
        }
        ShowNextDeletionIfIdle();   // a remote request may have queued meanwhile
    }

    private void OnDeletionsApplied(IReadOnlyCollection<string> touchedServiceIds)
    {
        if (touchedServiceIds.Count == 0) return;
        _remoteCleanupService.MarkInventoryDirty();
        RunOnUI(() =>
        {
            foreach (var svc in Services.Where(s => touchedServiceIds.Contains(s.Definition.Id)))
                svc.RefreshModels();
        });
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
        // The user asked for a tunnel: keep it up from now on, including when
        // this first attempt failed (no network at startup). DisconnectTunnel
        // stops the loop.
        _sshTunnelService.AutoReconnectLoop(GetCurrentlyTunnelablePorts);
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
        var ports = Services
            .Where(s => s.State != ServiceState.NotInstalled
                     && s.State != ServiceState.Installing)
            .Select(s => s.Definition.Port)
            .ToList();

        // Add the command bridge port when the feature is on, so the agent on
        // the VPS can reach the local bridge over the same tunnel (relay's
        // permitlisten whitelists this port — see relay.py BRIDGE_PORT). Takes
        // effect on the next (re)connect; OpenSettings reconnects when the user
        // flips the toggle with a live tunnel.
        var settings = _settingsService.Load();
        if (settings.AllowRemoteCommandBridge)
            ports.Add(settings.RemoteCommandBridgePort);

        return ports;
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectTunnel))]
    private async Task DisconnectTunnel()
    {
        await _sshTunnelService.Disconnect();
    }

    // ── Command bridge ("super-bridge") ──────────────────────────────────────

    /// <summary>
    /// Wire up the bridge built in App. The bridge calls the delegates below
    /// (on its own threads) to drive services and read GPU/state; we route its
    /// log lines to the activity log and keep IsBridgeActive in sync. Starts
    /// the listener if the feature is already enabled.
    /// </summary>
    public void AttachBridge(RemoteBridgeService bridge)
    {
        _remoteBridgeService = bridge;
        bridge.Log += msg => RunOnUI(() =>
        {
            AddLog($"[bridge] {msg}");
            IsBridgeActive = bridge.IsRunning;
        });

        var settings = _settingsService.Load();
        if (settings.IsPaired && settings.AllowRemoteCommandBridge
            && !string.IsNullOrEmpty(settings.RemoteCommandBridgeToken))
        {
            bridge.Start();
        }
        RunOnUI(() => IsBridgeActive = bridge.IsRunning);
    }

    // Snapshot the service list for /bridge/services — marshalled to the UI
    // thread because Services is an ObservableCollection bound to the view.
    public IReadOnlyList<BridgeServiceInfo> GetBridgeServices()
    {
        var d = System.Windows.Application.Current.Dispatcher;
        return d.Invoke(() => Services.Select(s => new BridgeServiceInfo(
            s.Definition.Id,
            s.Definition.DisplayName,
            s.Definition.Port,
            s.Definition.Category,
            s.State.ToString(),
            s.State != ServiceState.NotInstalled)).ToList());
    }

    public GpuInfo? GetBridgeGpu() => GpuInfo;

    public Task<bool> BridgeStartService(string id) => BridgeServiceControl(id, start: true);
    public Task<bool> BridgeStopService(string id) => BridgeServiceControl(id, start: false);

    private async Task<bool> BridgeServiceControl(string id, bool start)
    {
        var d = System.Windows.Application.Current.Dispatcher;
        return await d.InvokeAsync(async () =>
        {
            var vm = Services.FirstOrDefault(s => s.Definition.Id == id);
            if (vm == null) return false;
            if (start) await vm.StartCommand.ExecuteAsync(null);
            else await vm.StopCommand.ExecuteAsync(null);
            return true;
        }).Task.Unwrap();
    }

    /// <summary>
    /// One-click kill switch shown on the bridge banner: turn the feature off,
    /// persist it, and stop the listener immediately. The forwarded port stays
    /// in the tunnel until the next reconnect but nothing listens on it.
    /// </summary>
    [RelayCommand]
    private void StopBridge()
    {
        var settings = _settingsService.Load();
        settings.AllowRemoteCommandBridge = false;
        _settingsService.Save(settings);
        _remoteBridgeService?.Stop();
        IsBridgeActive = _remoteBridgeService?.IsRunning ?? false;
    }

    /// <summary>
    /// Reconcile the bridge listener + tunnel with the settings the user just
    /// saved. Called from OpenSettings after a Save.
    /// </summary>
    private async Task ReconcileBridge()
    {
        if (_remoteBridgeService is null) return;
        var settings = _settingsService.Load();
        bool shouldRun = settings.AllowRemoteCommandBridge
                         && !string.IsNullOrEmpty(settings.RemoteCommandBridgeToken);
        bool wasRunning = _remoteBridgeService.IsRunning;

        if (shouldRun)
        {
            _remoteBridgeService.Start();
            // Forward the bridge port now rather than on the next random drop:
            // GetCurrentlyTunnelablePorts already includes it, so a reconnect
            // re-establishes the forward set with the bridge port added.
            if (!wasRunning && IsTunnelConnected)
            {
                AddLog("[bridge] Reconnecting the tunnel to forward the bridge port…");
                await _sshTunnelService.Connect(settings, GetCurrentlyTunnelablePorts);
            }
        }
        else
        {
            _remoteBridgeService.Stop();
        }
        IsBridgeActive = _remoteBridgeService.IsRunning;
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
        var window = new Views.SettingsWindow(_settingsService, RepairFromSettings);
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

        // Reconcile the command bridge with the (possibly) changed toggle.
        await ReconcileBridge().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-pair from Settings (v0.9.26). The tunnel pins the VPS host-key
    /// fingerprints captured at /pair time, so once the relay moves to a
    /// new server (or the server rotates its SSH host keys) every connect
    /// fails with HOST KEY MISMATCH and only a fresh handshake fixes it.
    /// A live tunnel belongs to the old pairing — drop it first. Returns
    /// true when the pairing dialog completed successfully.
    /// </summary>
    private async Task<bool> RepairFromSettings(System.Windows.Window owner)
    {
        if (IsTunnelConnected)
            await DisconnectTunnel();

        var pairing = new Views.PairingWindow(_pairingService, _sshKeyService, _settingsService)
        {
            Owner = owner,
        };
        if (pairing.ShowDialog() != true)
            return false;

        IsPaired = true;
        await ConnectTunnel();
        return true;
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
