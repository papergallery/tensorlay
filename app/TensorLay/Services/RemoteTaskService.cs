using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

// Polls the relay's /api/tasks/pending endpoint while the user is paired and
// has the feature toggle ON. Each new task is surfaced through `TaskReceived`
// for the ViewModel to display an approval modal; status updates flow back
// via `PostStatusAsync`. The service holds no UI references — caller is
// responsible for marshaling events to the dispatcher.
//
// Lifecycle:
//   StartPolling() — spawn the background loop, no-op if already running.
//   StopPolling()  — cancel the loop, await its exit.
//   Dispose()      — Stop + dispose HttpClient.
//
// The loop is adaptive: 30 s when idle, 5 s for the next 3 polls after a
// task arrives (so a burst of agent-submitted tasks surfaces faster). Errors
// (network, 5xx) are logged via RemoteTaskLog and the loop continues at the
// idle interval so a transient relay outage doesn't poison the polling.
public class RemoteTaskService : IDisposable
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    private const int FastPollBudget = 3;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settingsService;
    private readonly ModelDownloader _modelDownloader;

    // Two HttpClients: a long-lived one for /api/tasks/* polls (default
    // 100s timeout is plenty for this), and a separate one for HEAD
    // preflight against arbitrary model URLs (5s tight bound, since we
    // don't want a slow CDN to delay the approval modal).
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _headHttp = new() { Timeout = HeadTimeout };

    // Tasks the user has already seen (modal shown or rejected) in this
    // session, keyed by task id. Prevents the modal from flashing again
    // between the approval click and the relay reflecting the new state
    // in the next /pending response.
    private readonly ConcurrentDictionary<string, byte> _seenTaskIds = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;
    private int _fastPollsRemaining;

    public RemoteTaskService(SettingsService settingsService, ModelDownloader modelDownloader)
    {
        _settingsService = settingsService;
        _modelDownloader = modelDownloader;
    }

    // Fired on a background thread when /api/tasks/pending returns a task
    // we haven't seen before. Subscribers must marshal to the UI thread
    // before touching WPF objects.
    public event Action<RemoteTask>? TaskReceived;

    // Diagnostic stream for the Logs page.
    public event Action<string>? RemoteTaskLog;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void StartPolling()
    {
        if (_disposed || IsRunning) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoop(_cts.Token));
        RemoteTaskLog?.Invoke("Started polling /api/tasks/pending.");
    }

    public async Task StopPolling()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try
        {
            if (_loopTask is not null)
                await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loopTask = null;
            RemoteTaskLog?.Invoke("Stopped polling.");
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool sawNew = await PollOnce(ct).ConfigureAwait(false);
                if (sawNew) _fastPollsRemaining = FastPollBudget;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RemoteTaskLog?.Invoke($"Poll failed: {ex.Message}");
                // Fall through to delay — never tight-loop on an error.
            }

            TimeSpan delay = _fastPollsRemaining > 0 ? FastInterval : IdleInterval;
            if (_fastPollsRemaining > 0) _fastPollsRemaining--;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> PollOnce(CancellationToken ct)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.RemoteTasksToken) || !settings.AllowRemoteInstallRequests)
            return false;

        string url = BaseUrl(settings) + "/api/tasks/pending";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Expected on the desktop side: 503 = relay not paired (ours
            // probably dropped its token files), 401/403 = our token is
            // stale (re-pair). Surface, don't crash.
            if ((int)resp.StatusCode is 401 or 403)
                RemoteTaskLog?.Invoke($"Auth rejected ({(int)resp.StatusCode}); re-pair to refresh token.");
            else
                RemoteTaskLog?.Invoke($"Pending poll: HTTP {(int)resp.StatusCode}");
            return false;
        }

        RemoteTaskListResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<RemoteTaskListResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            RemoteTaskLog?.Invoke($"Pending poll: bad JSON: {ex.Message}");
            return false;
        }
        if (body is null) return false;

        bool sawNew = false;
        foreach (var task in body.Tasks)
        {
            if (string.IsNullOrEmpty(task.Id)) continue;
            // Skip tasks we've already shown the user this session, plus
            // tasks already in approved/downloading state — those are
            // leftovers from a previous app run, and the desktop will
            // post `failed` for them via CleanupOrphanedTasks (see below).
            if (task.State != "pending") continue;
            if (!_seenTaskIds.TryAdd(task.Id, 0)) continue;

            // Auto-reject if the user has banned this agent label entirely.
            if (settings.RejectedAgentLabels.Contains(task.AgentLabel))
            {
                _ = PostStatusAsync(task.Id, "rejected", null, null);
                RemoteTaskLog?.Invoke($"Auto-rejected task from blocked source: {task.AgentLabel}");
                continue;
            }

            // Best-effort HEAD preflight — fills in the actual size from
            // Content-Length so the modal doesn't have to trust the
            // agent-supplied estimate. Failure is non-fatal.
            await TryHeadPreflight(task, ct).ConfigureAwait(false);

            sawNew = true;
            TaskReceived?.Invoke(task);
        }

        // Look for orphaned approved/downloading tasks from a prior run.
        // The desktop is the only entity that can drive those states forward,
        // so if we see them sitting there we know the previous attempt died
        // (app crash, machine reboot mid-download). Mark them failed so the
        // agent's GET sees a real terminal state.
        foreach (var orphan in body.Tasks)
        {
            if (orphan.State is "approved" or "downloading"
                && _seenTaskIds.TryAdd("orphan:" + orphan.Id, 0))
            {
                _ = PostStatusAsync(orphan.Id, "failed", null, "Desktop restarted before completion");
                RemoteTaskLog?.Invoke($"Reaped orphan task {orphan.Id[..8]} ({orphan.State})");
            }
        }

        return sawNew;
    }

    private async Task TryHeadPreflight(RemoteTask task, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, task.Url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeadTimeout);
            using var resp = await _headHttp.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long bytes)
            {
                task.ActualSizeMb = bytes / 1_048_576.0;
            }
        }
        catch
        {
            // 404, 405 (HEAD not supported), timeout — leave ActualSizeMb null.
        }
    }

    public async Task<bool> PostStatusAsync(string taskId, string state, double? progressPct, string? errorMsg, CancellationToken ct = default)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.RemoteTasksToken)) return false;

        string url = BaseUrl(settings) + "/api/tasks/" + Uri.EscapeDataString(taskId) + "/status";
        var payload = new RemoteTaskStatusUpdate
        {
            State = state,
            ProgressPct = progressPct,
            ErrorMsg = errorMsg,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                RemoteTaskLog?.Invoke($"Status post failed ({(int)resp.StatusCode}): {detail}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            RemoteTaskLog?.Invoke($"Status post error: {ex.Message}");
            return false;
        }
    }

    // Compute the target on-disk path for a remote task, mirroring the same
    // installDir + relativeInstallPath + modelsSubfolder layout used by the
    // per-service NewModelUrl flow. Returns null if the service id is unknown
    // or has no models subfolder configured.
    public string? ResolveTargetPath(RemoteTask task, string installDir)
    {
        var def = ServiceRegistry.GetAll().FirstOrDefault(s => s.Id == task.ServiceId);
        if (def is null || string.IsNullOrEmpty(def.ModelsSubfolder))
            return null;
        string fileName;
        try
        {
            fileName = Path.GetFileName(new Uri(task.Url).AbsolutePath);
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrEmpty(fileName)) return null;
        return Path.Combine(installDir, def.RelativeInstallPath, def.ModelsSubfolder, fileName);
    }

    public ModelDownloader Downloader => _modelDownloader;

    private static string BaseUrl(AppSettings settings)
        => $"http://{settings.VpsHost}:8090";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ = StopPolling(); } catch { /* best-effort */ }
        _http.Dispose();
        _headHttp.Dispose();
    }
}
