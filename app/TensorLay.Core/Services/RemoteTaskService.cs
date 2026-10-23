using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

// Outcome of a status POST. Callers that loop (progress updates, state-machine
// walks) need to distinguish a one-off network blip from a permanent reject —
// retrying a 400 just earns another 400 and inflates relay log noise.
public enum PostResult
{
    Ok,
    Transient,   // 5xx, network, timeout — safe to retry later
    Permanent,   // 4xx (illegal transition, bad payload, terminal state) — STOP
}

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

    // Two HttpClients: a long-lived one for /api/tasks/* polls and a
    // separate one for HEAD preflight against arbitrary model URLs (5s
    // tight bound, since we don't want a slow CDN to delay the approval
    // modal). Poll timeout is 35s — generous enough that a momentarily
    // loaded relay doesn't spam "HttpClient.Timeout of Xs elapsing"
    // entries (was 20s through v0.9.6, see punch list bug #3).
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };
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

    // Once a task earns a 4xx from /status, further POSTs for the same id
    // are dropped without hitting the wire. Cleared on Dispose only —
    // a permanently-rejected task stays rejected for the app's lifetime.
    private readonly ConcurrentDictionary<string, byte> _permanentlyFailedTasks = new();

    // Shared back-off floor for ALL status POSTs after a 5xx / network blip.
    // Stored as ticks so we can update atomically with Interlocked. Any caller
    // checks DateTime.UtcNow.Ticks against this value and bails early if the
    // relay is in cool-down. Reset to 0 on the next successful POST.
    private long _backoffUntilTicks;
    private int _consecutiveTransientFailures;

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

            // Skip-duplicate fast path: if the target file is already on
            // disk at the expected size, mark the task completed without
            // bothering the user. Saves a redundant 5-10 GB download when
            // an agent re-requests a model the user already has. We don't
            // verify SHA-256 here (would re-read the whole file every
            // poll); size match is the cheap signal. The agent can supply
            // a hash and we'll validate on actual download.
            string? alreadyOnDiskAt = ResolveTargetPath(task, settings.InstallDirectory);
            if (alreadyOnDiskAt is not null && File.Exists(alreadyOnDiskAt))
            {
                long actualBytes = new FileInfo(alreadyOnDiskAt).Length;
                long? expectedBytes = task.ActualSizeMb is double mb
                    ? (long)(mb * 1_048_576)
                    : null;
                // 1 MB tolerance — covers HEAD-vs-GET Content-Length drift
                // and tiny rounding from the MB conversion.
                if (expectedBytes is null || Math.Abs(actualBytes - expectedBytes.Value) < 1_048_576)
                {
                    // Walk the full state machine pending->approved->
                    // downloading->completed. Posting state="completed"
                    // directly from "pending" earns a 400 "Illegal
                    // transition" from relay (DESKTOP_TRANSITIONS in
                    // relay.py only permits adjacent steps). Three POSTs
                    // is fine — no-op transitions, fire-and-forget so the
                    // poll loop isn't gated on relay round-trips.
                    _ = SkipDuplicateAsync(task.Id, ct);
                    RemoteTaskLog?.Invoke(
                        $"Skipped task {task.Id[..8]}: {Path.GetFileName(alreadyOnDiskAt)} " +
                        $"already on disk ({actualBytes / 1_048_576} MB).");
                    continue;
                }
                RemoteTaskLog?.Invoke(
                    $"Task {task.Id[..8]}: file exists at {alreadyOnDiskAt} but size " +
                    $"({actualBytes / 1_048_576} MB) differs from expected " +
                    $"({(expectedBytes / 1_048_576) ?? 0} MB) — prompting user.");
            }

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

    // Walk a remote task through pending -> approved -> downloading ->
    // completed in three POSTs. Used by the skip-duplicate fast path in
    // PollOnce: when a model is already on disk we don't bother the user
    // with a modal, but we still need to drive relay state forward so the
    // agent that submitted the task sees a terminal "completed" instead
    // of forever-pending. Each PostStatusAsync call is awaited so a slow
    // first POST doesn't get overtaken by the second (which would replay
    // the original 400 "Illegal transition" bug from 0.9.5).
    private async Task SkipDuplicateAsync(string taskId, CancellationToken ct)
    {
        if (!await PostStatusAsync(taskId, "approved", null, null, ct).ConfigureAwait(false)) return;
        if (!await PostStatusAsync(taskId, "downloading", 100.0, null, ct).ConfigureAwait(false)) return;
        await PostStatusAsync(taskId, "completed", 100.0, null, ct).ConfigureAwait(false);
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

    // Back-compat shim — earlier callers expected bool. true == Ok.
    // New code should call PostStatusWithResultAsync directly so it can
    // distinguish Transient (worth retrying later) from Permanent (drop).
    public async Task<bool> PostStatusAsync(string taskId, string state, double? progressPct, string? errorMsg, CancellationToken ct = default)
        => await PostStatusWithResultAsync(taskId, state, progressPct, errorMsg, ct).ConfigureAwait(false) == PostResult.Ok;

    public async Task<PostResult> PostStatusWithResultAsync(string taskId, string state, double? progressPct, string? errorMsg, CancellationToken ct = default)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.RemoteTasksToken)) return PostResult.Permanent;

        // Drop fast: this task already earned a 4xx earlier. Re-posting only
        // produces another 4xx and another log line. Caller is expected to
        // notice Permanent and unsubscribe from progress events.
        if (_permanentlyFailedTasks.ContainsKey(taskId)) return PostResult.Permanent;

        // Honor shared back-off after recent 5xx / network failures so the
        // progress-event firehose doesn't keep slamming a sick relay.
        long backoffTicks = Interlocked.Read(ref _backoffUntilTicks);
        if (backoffTicks > 0 && DateTime.UtcNow.Ticks < backoffTicks)
            return PostResult.Transient;

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
            if (resp.IsSuccessStatusCode)
            {
                // Healthy round-trip — clear any back-off floor.
                Interlocked.Exchange(ref _backoffUntilTicks, 0);
                Interlocked.Exchange(ref _consecutiveTransientFailures, 0);
                return PostResult.Ok;
            }

            string detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;

            // 4xx is a contract problem (illegal transition, bad payload,
            // task not found, terminal-state conflict). Retrying does NOT
            // help. Mark this task permanent so the next progress event
            // skips the wire entirely.
            if (resp.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.NotFound
                or HttpStatusCode.Conflict
                or HttpStatusCode.UnprocessableEntity)
            {
                _permanentlyFailedTasks.TryAdd(taskId, 0);
                RemoteTaskLog?.Invoke($"Status post permanent ({code}) for {taskId[..Math.Min(8, taskId.Length)]}: {detail}");
                return PostResult.Permanent;
            }

            // 401/403 — token problem. Don't mark task permanent (re-pair
            // recovers it), but back off to avoid log-spamming the user
            // about an auth failure that won't resolve quickly.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ApplyBackoff();
                RemoteTaskLog?.Invoke($"Status post auth rejected ({code}); re-pair to refresh token.");
                return PostResult.Transient;
            }

            // 5xx and anything else unknown — treat as transient, back off.
            ApplyBackoff();
            RemoteTaskLog?.Invoke($"Status post transient ({code}): {detail}");
            return PostResult.Transient;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — not a relay problem, don't back off.
            return PostResult.Transient;
        }
        catch (Exception ex)
        {
            ApplyBackoff();
            RemoteTaskLog?.Invoke($"Status post error: {ex.Message}");
            return PostResult.Transient;
        }
    }

    // Exponential 1s, 2s, 4s, ... capped at 64s. Reset when any POST succeeds.
    private void ApplyBackoff()
    {
        int n = Interlocked.Increment(ref _consecutiveTransientFailures);
        int delaySec = Math.Min(64, 1 << Math.Min(n - 1, 6));
        long until = DateTime.UtcNow.AddSeconds(delaySec).Ticks;
        Interlocked.Exchange(ref _backoffUntilTicks, until);
    }

    // Compute the target on-disk path for a remote task, mirroring the same
    // installDir + relativeInstallPath + modelsSubfolder layout used by the
    // per-service NewModelUrl flow. Returns null if the service id is unknown
    // or has no models subfolder configured.
    //
    // When the relay supplies an optional `target_subdir` field (v1.3.3+) the
    // download is rerouted under the service's ModelsScanRoot (e.g. "models"
    // for ComfyUI) so that e.g. target_subdir="loras" lands at
    // <installDir>/comfyui/models/loras/<filename> instead of the legacy
    // <installDir>/comfyui/models/checkpoints/<filename>. The field is
    // sanitized defensively before use; on rejection we log a warning and
    // fall back to the legacy ModelsSubfolder.
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

        // Default routing — legacy ModelsSubfolder (e.g. "models/checkpoints").
        string relativeFolder = def.ModelsSubfolder;

        if (!string.IsNullOrWhiteSpace(task.TargetSubdir))
        {
            string? sanitized = SanitizeTargetSubdir(task.TargetSubdir);
            if (sanitized is not null)
            {
                // Anchor under ModelsScanRoot when defined (the parent that
                // holds checkpoints/, loras/, vae/, …); otherwise fall back
                // to ModelsSubfolder's parent. Split on '/' so a subdir like
                // "insightface/models/antelopev2" translates to platform path
                // separators on Windows.
                string root = string.IsNullOrEmpty(def.ModelsScanRoot)
                    ? def.ModelsSubfolder
                    : def.ModelsScanRoot;
                string[] segments = sanitized.Split('/');
                relativeFolder = Path.Combine(root, Path.Combine(segments));
            }
            else
            {
                RemoteTaskLog?.Invoke(
                    $"WARNING: rejected target_subdir '{task.TargetSubdir}' for task " +
                    $"{task.Id[..Math.Min(8, task.Id.Length)]}; falling back to " +
                    $"'{def.ModelsSubfolder}'.");
            }
        }

        return Path.Combine(installDir, def.RelativeInstallPath, relativeFolder, fileName);
    }

    // Defense-in-depth check on `target_subdir` before we let it shape a
    // filesystem path. The relay already validates with the same rules, but
    // the desktop owns the disk and cannot outsource that trust. Returns the
    // input verbatim when it passes, or null when it should be rejected (the
    // caller logs and falls back to the legacy default).
    //
    // Rules:
    //   - null / empty / whitespace -> reject
    //   - leading or trailing '/'   -> reject
    //   - any backslash             -> reject (Path.Combine treats '\\' as a
    //                                  separator on Windows; could escape the
    //                                  models tree if combined with "..")
    //   - segment '.' or '..'       -> reject (path traversal)
    //   - segment starting with '.' -> reject (hidden / dotfile abuse)
    //   - char outside [A-Za-z0-9_/-] anywhere -> reject
    //   - more than 4 segments      -> reject
    private static string? SanitizeTargetSubdir(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim();
        if (s.Contains('\\')) return null;
        if (s.StartsWith('/') || s.EndsWith('/')) return null;

        foreach (char c in s)
        {
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   || c == '_' || c == '-' || c == '/';
            if (!ok) return null;
        }

        string[] segments = s.Split('/');
        if (segments.Length == 0 || segments.Length > 4) return null;
        foreach (string seg in segments)
        {
            if (seg.Length == 0) return null;        // empty segment / "//" etc.
            if (seg == "." || seg == "..") return null;
            if (seg.StartsWith('.')) return null;
        }

        return s;
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
