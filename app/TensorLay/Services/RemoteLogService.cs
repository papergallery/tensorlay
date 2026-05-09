using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TensorLay.Services;

// Polls /api/logs/pending while the user is paired and Settings.AllowRemote-
// LogRequests is on. For each new request: gather recent DownloadLog files
// + crash.log, cap to MaxUploadBytes, POST to /api/logs/{id}. When the
// toggle is off every request is auto-rejected with a reason so the agent
// doesn't spin. Mirrors RemoteTaskService's polling shape but with a much
// simpler state machine (pending → completed | rejected).
//
// Runs unconditionally — the per-poll setting check inside PollOnce keeps
// behavior consistent with the install-request flow (RemoteTaskService
// also starts always; the toggle gates work, not the loop).
public class RemoteLogService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    // 5 MB cap on the desktop side. Relay caps at 10 MB so we have headroom
    // for a small framing overhead (HTTP, etc.) without the upload getting
    // 413'd from the server. Truncation strategy: keep the metadata header
    // and tail (most-recent content) of the body.
    private const long MaxUploadBytes = 5L * 1024 * 1024;

    private readonly SettingsService _settingsService;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    // In-memory dedup so a slow upload doesn't get overlapped by the next
    // poll seeing the same row still as "pending" (relay won't flip it
    // until our POST returns). Clears on Dispose; fresh process = fresh set,
    // which is what we want — if the desktop crashed mid-upload, the relay
    // row is still pending and the new instance should retry.
    private readonly HashSet<string> _seenIds = new();

    public RemoteLogService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event Action<string>? Log;
    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void StartPolling()
    {
        if (_disposed || IsRunning) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoop(_cts.Token));
        Log?.Invoke("Started polling /api/logs/pending.");
    }

    public async Task StopPolling()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try
        {
            if (_loopTask is not null) await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loopTask = null;
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnce(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Poll error: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.RemoteTasksToken)) return;

        string baseUrl = $"http://{settings.VpsHost}:8090";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/logs/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Pre-1.5.0 relays don't have this endpoint — silent no-op.
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            // 401/403 = stale token (re-pair will fix); other codes = transient.
            if ((int)resp.StatusCode is 401 or 403)
                Log?.Invoke($"Auth rejected ({(int)resp.StatusCode}); re-pair to refresh token.");
            return;
        }

        LogsPendingResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<LogsPendingResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException) { return; }
        if (body is null) return;

        foreach (var r in body.Requests)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            if (!_seenIds.Add(r.Id)) continue;

            if (!settings.AllowRemoteLogRequests)
            {
                _ = RejectAsync(baseUrl, settings.RemoteTasksToken, r.Id,
                    "AllowRemoteLogRequests is OFF — enable in Settings to share logs.", ct);
                Log?.Invoke($"Auto-rejected log request {r.Id[..Math.Min(8, r.Id.Length)]} (toggle off)");
                continue;
            }

            // Run the upload off the polling task so a slow disk read or
            // upload doesn't stall the next /api/logs/pending check.
            _ = HandleRequestAsync(baseUrl, settings.RemoteTasksToken, r, ct);
        }
    }

    private async Task HandleRequestAsync(string baseUrl, string token, LogRequest request, CancellationToken ct)
    {
        string idHandle = request.Id[..Math.Min(8, request.Id.Length)];
        try
        {
            byte[] payload = GatherLogs(request.MaxAgeDays);
            if (payload.Length == 0)
            {
                await RejectAsync(baseUrl, token, request.Id,
                    "No log files found for the requested window.", ct);
                Log?.Invoke($"Log request {idHandle}: nothing to send, rejected.");
                return;
            }

            await UploadAsync(baseUrl, token, request.Id, payload, ct);
            Log?.Invoke($"Log request {idHandle}: uploaded {payload.Length} bytes");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Log request {idHandle} failed: {ex.Message}");
            try
            {
                await RejectAsync(baseUrl, token, request.Id,
                    $"Desktop error: {ex.Message}", ct);
            }
            catch { /* best-effort */ }
        }
    }

    private async Task RejectAsync(string baseUrl, string token, string id, string reason, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/logs/{id}/reject")
        {
            Content = JsonContent.Create(new { reason }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            // We don't surface non-2xx here — caller already gave up on this id.
        }
        catch { /* swallow — best-effort */ }
    }

    private async Task UploadAsync(string baseUrl, string token, string id, byte[] payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/logs/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new ByteArrayContent(payload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // Bundles app metadata + recent downloads-*.log files + crash.log into a
    // single text blob. Files older than maxAgeDays are skipped. Output is
    // capped at MaxUploadBytes by truncating the body (NOT the header) from
    // the front, so the agent always sees the metadata + most recent content.
    private static byte[] GatherLogs(int maxAgeDays)
    {
        var hdr = new StringBuilder();
        hdr.AppendLine("=== TensorLay diagnostic bundle ===");
        hdr.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        hdr.AppendLine($"App version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        hdr.AppendLine($"OS: {Environment.OSVersion}");
        hdr.AppendLine($".NET: {Environment.Version}");
        hdr.AppendLine($"Machine: {Environment.MachineName}");
        hdr.AppendLine($"Max age days requested: {maxAgeDays}");
        hdr.AppendLine();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logsDir = Path.Combine(appData, "TensorLay", "logs");
        var sections = new List<string>();

        if (Directory.Exists(logsDir))
        {
            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, maxAgeDays));
            // Newest first so when we truncate-from-front later, the freshest
            // log content is the LAST thing in the body and therefore preserved.
            foreach (var path in Directory.GetFiles(logsDir, "downloads-*.log")
                                          .OrderBy(p => p))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTime < cutoff) continue;
                    sections.Add($"--- {Path.GetFileName(path)} ---\n{File.ReadAllText(path)}\n");
                }
                catch (Exception ex)
                {
                    sections.Add($"--- {Path.GetFileName(path)} ---\n[read error: {ex.Message}]\n");
                }
            }
        }

        // crash.log is the canonical app-level fault log. Always include if
        // present — it's the highest-signal piece for diagnosing failures.
        string crashLog = Path.Combine(appData, "TensorLay", "crash.log");
        if (File.Exists(crashLog))
        {
            try
            {
                sections.Add($"--- crash.log ---\n{File.ReadAllText(crashLog)}\n");
            }
            catch (Exception ex)
            {
                sections.Add($"--- crash.log ---\n[read error: {ex.Message}]\n");
            }
        }

        var headerBytes = Encoding.UTF8.GetBytes(hdr.ToString());
        var bodyBytes = Encoding.UTF8.GetBytes(string.Join("\n", sections));

        long budget = MaxUploadBytes - headerBytes.Length;
        if (budget <= 0) return headerBytes; // pathological — header alone over cap

        if (bodyBytes.Length > budget)
        {
            var marker = Encoding.UTF8.GetBytes("[... truncated for size — older content omitted ...]\n");
            int keep = (int)Math.Max(0, budget - marker.Length);
            var truncated = new byte[marker.Length + keep];
            Buffer.BlockCopy(marker, 0, truncated, 0, marker.Length);
            Buffer.BlockCopy(bodyBytes, bodyBytes.Length - keep, truncated, marker.Length, keep);
            bodyBytes = truncated;
        }

        var combined = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, combined, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, combined, headerBytes.Length, bodyBytes.Length);
        return combined;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ = StopPolling(); } catch { /* best-effort */ }
        _http.Dispose();
    }

    // Wire shape mirroring relay's response (snake_case JSON).
    private class LogRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("max_age_days")] public int MaxAgeDays { get; set; } = 7;
        [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
        [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
    }

    private class LogsPendingResponse
    {
        [JsonPropertyName("requests")] public List<LogRequest> Requests { get; set; } = new();
    }
}
