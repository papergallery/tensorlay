using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using TensorLay.Services;
using TensorLay.ViewModels;
using TensorLay.Views;
namespace TensorLay;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\TensorLay-SingleInstance-9F8E";
    private const string UrlSchemePipeName = "TensorLay-UrlScheme-9F8E";
    private const string UrlScheme = "tensorlay";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;

    // Crash logging to %APPDATA%\TensorLay\crash.log so users can hand us
    // a stack trace after a silent termination — added in 0.9.5 after a
    // user reported "the app vanishes after model downloads, no logs".
    // Three sources: AppDomain (truly fatal native/CLR), Dispatcher (UI
    // thread async-void handlers — most common), TaskScheduler (unobserved
    // Task exceptions that GC eventually surfaces).
    private static readonly object _crashLogLock = new();
    private static string CrashLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TensorLay", "crash.log");

    private ProcessManager? _processManager;
    private HealthCheckService? _healthCheckService;
    private InstallerService? _installerService;
    private ModelDownloader? _modelDownloader;
    private RemoteTaskService? _remoteTaskService;
    private RemoteLogService? _remoteLogService;
    private GpuMonitor? _gpuMonitor;
    private SshTunnelService? _sshTunnelService;
    private AccountService? _accountService;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Wire up crash logging FIRST — before any other code runs, so
        // even an exception during service-construction is captured. The
        // three sinks cover: AppDomain (CLR-level), Dispatcher (WPF UI
        // thread, the common one for async-void handlers), TaskScheduler
        // (unobserved Task exceptions that surface during GC).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException",
                args.ExceptionObject as Exception, fatal: args.IsTerminating);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher.UnhandledException", args.Exception, fatal: false);
            // Mark as handled so WPF doesn't tear the app down — the user
            // can keep working, and we've already captured the trace.
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    "An error occurred and was logged to:\n" + CrashLogPath +
                    "\n\nDetails:\n" + args.Exception.Message,
                    "TensorLay — error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* dispatcher reentrancy — give up gracefully */ }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception, fatal: false);
            args.SetObserved();
        };

        // OnStartup is async void — without this top-level catch, any
        // exception between Show() and the update block crashes the app
        // with no diagnostic. Keep the body wrapped so we can at least
        // surface the error before exiting.
        try
        {
            await StartupAsync(e);
        }
        catch (Exception ex)
        {
            LogCrash("StartupAsync", ex, fatal: true);
            try
            {
                MessageBox.Show(
                    $"TensorLay failed to start:\n\n{ex.Message}\n\nLog: {CrashLogPath}",
                    "TensorLay",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* dispatcher may already be down */ }
            Shutdown(1);
        }
    }

    private static void LogCrash(string source, Exception? ex, bool fatal)
    {
        if (ex is null) return;
        try
        {
            string dir = Path.GetDirectoryName(CrashLogPath)!;
            Directory.CreateDirectory(dir);
            // Lock so concurrent fires (Dispatcher + TaskScheduler racing
            // on the same root cause) produce a clean append, not a
            // shredded interleave.
            lock (_crashLogLock)
            {
                using var sw = new StreamWriter(CrashLogPath, append: true);
                sw.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{(fatal ? " [FATAL]" : "")}");
                sw.WriteLine(ex.ToString());
                sw.WriteLine(new string('-', 80));
            }
        }
        catch
        {
            // Logging itself failed — nothing useful to do (we'd just
            // recurse into the same handler). Swallow.
        }
    }

    private async Task StartupAsync(StartupEventArgs e)
    {
        // One-time migration of legacy %APPDATA%\GpuHub → %APPDATA%\TensorLay
        // (pre-rename installs stored settings under the old name).
        MigrateLegacyAppData();

        // ── Single-instance: forward URL to existing instance and exit ──
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            string? incomingUrl = FindUrlArg(e.Args);
            if (incomingUrl != null) TryForwardUrl(incomingUrl);
            Shutdown();
            return;
        }

        // Start the URL pipe server BEFORE any heavy init — the second
        // instance only retries pipe.Connect for 2 seconds, and our
        // MainWindow.Show() can easily take longer than that on cold
        // start. Running it before init guarantees forwards aren't lost.
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => RunUrlPipeServerAsync(_pipeCts.Token));

        base.OnStartup(e);

        var settingsService = new SettingsService();
        _accountService = new AccountService();
        _accountService.LoadCached();
        _gpuMonitor = new GpuMonitor();
        _processManager = new ProcessManager();
        _healthCheckService = new HealthCheckService();
        _sshTunnelService = new SshTunnelService(settingsService);
        _installerService = new InstallerService();
        _modelDownloader = new ModelDownloader(settingsService);
        _remoteTaskService = new RemoteTaskService(settingsService, _modelDownloader);
        // Remote log service polls /api/logs/pending. Loop runs always; the
        // AllowRemoteLogRequests setting check happens per-request inside
        // PollOnce so toggling it doesn't require a restart.
        _remoteLogService = new RemoteLogService(settingsService);
        _remoteLogService.StartPolling();

        var pairingService = new PairingService();
        var sshKeyService = new SshKeyService();

        var mainVm = new MainViewModel(
            _processManager,
            _healthCheckService,
            _installerService,
            _modelDownloader,
            settingsService,
            _gpuMonitor,
            _sshTunnelService,
            pairingService,
            sshKeyService,
            _remoteTaskService);

        mainVm.Initialize();

        // Build the updater up-front so we can stamp the Title BEFORE Show()
        // — otherwise the user sees "TensorLay" briefly before it flashes to
        // "TensorLay v0.7.9" once the update check kicks in.
        using var updater = new AutoUpdater();

        // Show main window
        _mainWindow = new MainWindow
        {
            DataContext = mainVm,
            Title = $"TensorLay v{updater.CurrentVersion}",
        };
        _mainWindow.Show();

        // Trigger autoconnect AFTER MainWindow.Show — in the corrupted-
        // settings edge case (AutoconnectTunnel=true but IsPaired=false)
        // ConnectTunnel() opens a PairingWindow whose Owner is set from
        // Application.Current.MainWindow. Doing this before Show() leaves
        // that owner null and the dialog ends up unparented.
        var settings = settingsService.Load();
        if (settings.AutoconnectTunnel)
            _ = mainVm.ConnectTunnelCommand.ExecuteAsync(null);

        // Process the URL the OS launched us with, if any
        string? initialUrl = FindUrlArg(e.Args);
        if (initialUrl != null) _ = HandleUrlAsync(initialUrl);

        // Check for updates
        try
        {
            updater.UpdateLog += msg => System.Diagnostics.Debug.WriteLine($"[updater] {msg}");
            var (available, newVersion) = await updater.CheckForUpdate();

            if (available && newVersion != null)
            {
                var updateWindow = new UpdateWindow(updater, newVersion);
                updateWindow.Owner = _mainWindow;
                updateWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var accepted = updateWindow.ShowDialog() == true && updateWindow.UserAccepted;
                if (accepted)
                {
                    Shutdown();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[updater] update error: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pipeCts?.Cancel(); } catch { /* ignore */ }
        _processManager?.Dispose();
        _healthCheckService?.Dispose();
        _remoteTaskService?.Dispose();
        _remoteLogService?.Dispose();
        _modelDownloader?.Dispose();
        _gpuMonitor?.Dispose();
        _sshTunnelService?.Dispose();

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned */ }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    // ── URL scheme plumbing ────────────────────────────────────────────────

    private static string? FindUrlArg(string[] args)
    {
        return args.FirstOrDefault(a =>
            a.StartsWith(UrlScheme + "://", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryForwardUrl(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", UrlSchemePipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var sw = new StreamWriter(pipe);
            sw.WriteLine(url);
            sw.Flush();
        }
        catch
        {
            // Best-effort — if the pipe is gone the other instance is likely shutting down.
        }
    }

    private async Task RunUrlPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    UrlSchemePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var sr = new StreamReader(pipe);
                string? url = await sr.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    string captured = url;
                    _ = Dispatcher.InvokeAsync(() => HandleUrlAsync(captured));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task HandleUrlAsync(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return;
        if (!string.Equals(uri.Scheme, UrlScheme, StringComparison.OrdinalIgnoreCase)) return;

        BringToForeground();

        if (!string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase)) return;

        string? token = ParseQueryParam(uri.Query, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(_mainWindow,
                "Sign-in URL is missing a token.",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_accountService == null) return;

        try
        {
            var session = await _accountService.SignInWithTokenAsync(token);
            MessageBox.Show(_mainWindow,
                $"Signed in as {session.Username} ({session.Email}).",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_mainWindow,
                $"Sign-in failed: {ex.Message}",
                "TensorLay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // REMOVE in v0.10+: one-shot rename of %APPDATA%\GpuHub → %APPDATA%\TensorLay
    // for users updating from v0.7.x. After two or three releases past 0.8.0
    // the install base will have all migrated and this code is dead weight.
    // The early-return on Directory.Exists(newDir) makes it free on every
    // launch after the first, so leaving it for now is harmless.
    private static void MigrateLegacyAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string oldDir = Path.Combine(appData, "GpuHub");
        string newDir = Path.Combine(appData, "TensorLay");
        if (Directory.Exists(newDir)) return;
        if (!Directory.Exists(oldDir)) return;
        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[migration] could not move {oldDir} → {newDir}: {ex.Message}");
        }
    }

    private static string? ParseQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (query.StartsWith("?")) query = query.Substring(1);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            string value = eq >= 0 ? pair.Substring(eq + 1) : "";
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(value);
        }
        return null;
    }

    private void BringToForeground()
    {
        if (_mainWindow == null) return;
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }
}
