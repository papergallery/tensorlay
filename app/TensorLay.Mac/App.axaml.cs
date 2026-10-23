using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TensorLay.Mac.Views;
using TensorLay.Platform;
using TensorLay.Services;
using TensorLay.ViewModels;

namespace TensorLay.Mac;

// macOS counterpart of the WPF App: builds the same services and view model,
// wires the command bridge, auto-connects, handles tensorlay:// sign-in links
// and checks for updates. Single-instance is LaunchServices' job on macOS
// (opening the app or a tensorlay:// link again activates this instance and
// arrives here as an activation event).
public partial class App : Application
{
    private const string UrlScheme = "tensorlay";

    private static readonly object CrashLogLock = new();

    private ProcessManager? _processManager;
    private HealthCheckService? _healthCheckService;
    private ModelDownloader? _modelDownloader;
    private RemoteTaskService? _remoteTaskService;
    private RemoteLogService? _remoteLogService;
    private RemoteCleanupService? _remoteCleanupService;
    private GpuMonitor? _gpuMonitor;
    private SshTunnelService? _sshTunnelService;
    private AccountService? _accountService;
    private RemoteBridgeService? _remoteBridgeService;
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Crash logging first, same three sinks as Windows (AppDomain, UI
        // dispatcher, unobserved tasks) into <data>/crash.log, which the
        // remote-logs feature can ship to the agent.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception, args.IsTerminating);
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher.UnhandledException", args.Exception, fatal: false);
            args.Handled = true;
            try
            {
                UiHost.Current.ShowMessage("TensorLay — error",
                    $"An error occurred and was logged to:\n{AppPaths.CrashLogPath}\n\nDetails:\n{args.Exception.Message}",
                    MessageKind.Warning);
            }
            catch { /* UI not ready */ }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception, fatal: false);
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => DisposeServices();
            try
            {
                Startup(desktop);
            }
            catch (Exception ex)
            {
                LogCrash("Startup", ex, fatal: true);
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Startup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        AppPaths.EnsureDataDir();

        var settingsService = new SettingsService();
        var pairingService = new PairingService();
        var sshKeyService = new SshKeyService();
        UiHost.Current = new MacUiHost(settingsService, pairingService, sshKeyService);

        _accountService = new AccountService();
        _accountService.LoadCached();
        _gpuMonitor = new GpuMonitor();
        _processManager = new ProcessManager();
        _healthCheckService = new HealthCheckService();
        _sshTunnelService = new SshTunnelService(settingsService);
        var installerService = new InstallerService();
        _modelDownloader = new ModelDownloader(settingsService);
        _remoteTaskService = new RemoteTaskService(settingsService, _modelDownloader);
        _remoteLogService = new RemoteLogService(settingsService);
        _remoteLogService.StartPolling();
        _remoteCleanupService = new RemoteCleanupService(settingsService, _modelDownloader);
        _remoteCleanupService.StartPolling();
        var modelDeletionService = new ModelDeletionService(_modelDownloader, settingsService);

        var mainVm = new MainViewModel(
            _processManager, _healthCheckService, installerService, _modelDownloader,
            settingsService, _gpuMonitor, _sshTunnelService, pairingService, sshKeyService,
            _remoteTaskService, _remoteCleanupService, modelDeletionService);
        mainVm.Initialize();

        _remoteBridgeService = new RemoteBridgeService(
            settingsService,
            mainVm.GetBridgeServices,
            mainVm.BridgeStartService,
            mainVm.BridgeStopService,
            mainVm.GetBridgeGpu);
        mainVm.AttachBridge(_remoteBridgeService);

        var updater = new AutoUpdater();
        _mainWindow = new MainWindow
        {
            DataContext = mainVm,
            Title = $"TensorLay v{updater.CurrentVersion}",
        };
        desktop.MainWindow = _mainWindow;

        // tensorlay:// links: LaunchServices delivers them to the running
        // instance as protocol activations (cold start included).
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs p)
                    Dispatcher.UIThread.Post(() => _ = HandleUrlAsync(p.Uri));
            };
        }

        // After the window is up: autoconnect (may open the pairing dialog,
        // which needs an owner) and the update check.
        _mainWindow.Opened += async (_, _) =>
        {
            var settings = settingsService.Load();
            LoginItem.Apply(settings.AutostartWithWindows);
            if (settings.AutoconnectTunnel)
                _ = mainVm.ConnectTunnelCommand.ExecuteAsync(null);
            await CheckForUpdatesAsync(updater);
        };
    }

    private async Task CheckForUpdatesAsync(AutoUpdater updater)
    {
        try
        {
            var (available, newVersion) = await updater.CheckForUpdate();
            if (!available || newVersion is null || _mainWindow is null) return;

            var window = new UpdateWindow(updater, newVersion);
            if (await window.ShowDialog<bool>(_mainWindow))
            {
                // The swap script waits for this process to exit.
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[updater] update error: {ex.Message}");
        }
    }

    private async Task HandleUrlAsync(Uri uri)
    {
        if (!string.Equals(uri.Scheme, UrlScheme, StringComparison.OrdinalIgnoreCase)) return;
        _mainWindow?.Activate();
        if (!string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase)) return;

        string? token = ParseQueryParam(uri.Query, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            UiHost.Current.ShowMessage("TensorLay", "Sign-in URL is missing a token.", MessageKind.Warning);
            return;
        }
        if (_accountService is null) return;

        try
        {
            var session = await _accountService.SignInWithTokenAsync(token);
            UiHost.Current.ShowMessage("TensorLay", $"Signed in as {session.Username} ({session.Email}).", MessageKind.Info);
        }
        catch (Exception ex)
        {
            UiHost.Current.ShowMessage("TensorLay", $"Sign-in failed: {ex.Message}", MessageKind.Error);
        }
    }

    private static string? ParseQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(eq >= 0 ? pair[(eq + 1)..] : "");
        }
        return null;
    }

    private void DisposeServices()
    {
        _processManager?.Dispose();
        _healthCheckService?.Dispose();
        _remoteBridgeService?.Dispose();
        _remoteTaskService?.Dispose();
        _remoteLogService?.Dispose();
        _remoteCleanupService?.Dispose();
        _modelDownloader?.Dispose();
        _gpuMonitor?.Dispose();
        _sshTunnelService?.Dispose();
    }

    private static void LogCrash(string source, Exception? ex, bool fatal)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureDataDir();
            lock (CrashLogLock)
            {
                using var sw = new StreamWriter(AppPaths.CrashLogPath, append: true);
                sw.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{(fatal ? " [FATAL]" : "")}");
                sw.WriteLine(ex.ToString());
                sw.WriteLine(new string('-', 80));
            }
        }
        catch
        {
            // Logging itself failed — nothing useful to do.
        }
    }
}
