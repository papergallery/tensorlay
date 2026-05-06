using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

public class ModelDownloader : IDisposable
{
    // .pth covers ESRGAN/upscalers (e.g. 4x-UltraSharp), .bin for some
    // CLIP/embedding files, plus the SD checkpoint formats and gguf for
    // quantized LLM weights. Keeping the list in one place so adding a
    // new format is a one-line change.
    private static readonly string[] ModelExtensions = { "*.safetensors", "*.ckpt", "*.gguf", "*.pth", "*.bin" };

    // Per-chunk idle (between-reads) timeout. With HttpClient.Timeout disabled
    // we still need an escape from a TCP socket that stays alive but stops
    // delivering bytes — this is what kills xet-bridge wedges on HF without
    // killing slow-but-progressing connections.
    private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(60);

    // Resume threshold: partials below this are dropped on restart, not
    // resumed via Range. A tiny on-disk file from a previous attempt is
    // more likely an HTML error page or a redirect body than real bytes,
    // and Range:bytes=N- against a server that ignores it would silently
    // glue a fresh full body onto the bogus prefix. 100 MB is the same
    // cutoff InstallerService uses for the Ollama installer.
    private const long ResumeMinBytes = 100L * 1024 * 1024;

    // Streaming downloads can run for many minutes on multi-GB checkpoints.
    // The default HttpClient.Timeout of 100s applies to the *entire* request
    // including body streaming (even with HttpCompletionOption.ResponseHeadersRead),
    // so a 12 GB Flux model at 50 MB/s would abort around the 5 GB mark.
    // Disable the global timeout — hang detection is the cancellation token
    // plus the read-loop's natural EOF/exception handling.
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    // Separate, short-timeout client for Ollama HTTP API calls (/api/tags
    // polling, /api/delete). Default HttpClient.Timeout (100s) would freeze
    // the Models tab if Ollama is unreachable — 2s is plenty for /api/tags.
    // /api/delete uses a fresh CTS with a longer budget (see DeleteOllamaModelAsync).
    private readonly HttpClient _ollamaApiClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentDictionary<string, DownloadTask> _activeTasks = new();
    private readonly SettingsService _settingsService;
    private bool _disposed;

    public ModelDownloader(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event Action<DownloadTask>? DownloadProgressChanged;
    public event Action<DownloadTask>? DownloadCompleted;

    public DownloadTask StartDownload(string url, string targetPath, string serviceId)
    {
        // Fast path: existing in-flight task for this URL — return it.
        if (_activeTasks.TryGetValue(url, out var existing))
            return existing;

        // Build a candidate; if TryAdd loses the race, drop it and return
        // whatever the winning thread put in. This is the atomic
        // check-then-add idiom for ConcurrentDictionary — without TryAdd,
        // two concurrent StartDownload calls for the same URL would both
        // start RunDownload and race two writers to the same file.
        var candidate = new DownloadTask
        {
            Url = url,
            TargetPath = targetPath,
            ServiceId = serviceId,
            State = DownloadState.Pending,
            CancellationTokenSource = new CancellationTokenSource()
        };

        if (_activeTasks.TryAdd(url, candidate))
        {
            _ = RunDownload(candidate);
            return candidate;
        }

        // Lost the race — dispose our orphan CTS so it doesn't leak,
        // return the winner's task. If the winner already finished and
        // removed itself between TryAdd and TryGetValue, fall through to
        // a fresh start with the candidate (RunDownload still safe to
        // call once).
        candidate.CancellationTokenSource.Dispose();
        if (_activeTasks.TryGetValue(url, out var winner))
            return winner;

        var fresh = new DownloadTask
        {
            Url = url,
            TargetPath = targetPath,
            ServiceId = serviceId,
            State = DownloadState.Pending,
            CancellationTokenSource = new CancellationTokenSource()
        };
        _activeTasks[url] = fresh;
        _ = RunDownload(fresh);
        return fresh;
    }

    public void CancelDownload(DownloadTask task)
    {
        task.CancellationTokenSource.Cancel();
    }

    private async Task RunDownload(DownloadTask task)
    {
        task.State = DownloadState.Downloading;
        var userCt = task.CancellationTokenSource.Token;

        try
        {
            string? dir = Path.GetDirectoryName(task.TargetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Inspect any partial from a previous attempt. Below the
            // threshold → discard (likely a redirect/error body); at or
            // above → ask the server to resume via Range.
            long resumeFrom = 0;
            if (File.Exists(task.TargetPath))
            {
                long existing = new FileInfo(task.TargetPath).Length;
                if (existing >= ResumeMinBytes)
                    resumeFrom = existing;
                else if (existing > 0)
                    File.Delete(task.TargetPath);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, task.Url);
            if (resumeFrom > 0)
                req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            using var response = await _httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                userCt).ConfigureAwait(false);

            // 416 Range Not Satisfiable on resume usually means the partial
            // already equals (or exceeds) the full size — treat as a "we're
            // done, just need to flush the state machine" success rather
            // than a hard fail. Caller's SHA-256 verify (when present) is
            // the correctness backstop.
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                task.TotalBytes = resumeFrom;
                task.BytesDownloaded = resumeFrom;
                task.ProgressPercent = 100;
                task.State = DownloadState.Completed;
                return;
            }

            // Server ignored our Range and replied 200 with the whole body —
            // we must NOT append it to the partial (that would corrupt the
            // file). Truncate and start over.
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                File.Delete(task.TargetPath);
                resumeFrom = 0;
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }

            // ContentLength on a 206 is the *remaining* bytes — add the
            // resume offset to recover the true total for progress display.
            long? remaining = response.Content.Headers.ContentLength;
            task.TotalBytes = remaining is > 0 ? remaining.Value + resumeFrom : 0;
            task.BytesDownloaded = resumeFrom;

            await using var stream = await response.Content.ReadAsStreamAsync(userCt);
            await using var file = new FileStream(
                task.TargetPath,
                resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write);

            byte[] buffer = new byte[81920];
            while (true)
            {
                // Per-read idle timeout linked to the user-cancel token, so
                // either a user click or a 60s zero-byte stall ends the read.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
                idleCts.CancelAfter(IdleReadTimeout);

                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(), idleCts.Token);
                }
                catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
                {
                    // Idle timeout (not user cancel). Surface as a stall —
                    // partial stays on disk so the next attempt resumes.
                    throw new IOException(
                        $"Download stalled — no data in {IdleReadTimeout.TotalSeconds:0}s " +
                        $"(got {task.BytesDownloaded / 1_048_576} MB of " +
                        $"{(task.TotalBytes > 0 ? (task.TotalBytes / 1_048_576).ToString() + " MB" : "unknown size")}).");
                }
                if (bytesRead == 0) break;

                await file.WriteAsync(buffer.AsMemory(0, bytesRead), userCt);
                task.BytesDownloaded += bytesRead;
                task.ProgressPercent = task.TotalBytes > 0
                    ? (double)task.BytesDownloaded / task.TotalBytes * 100.0
                    : 0;
                DownloadProgressChanged?.Invoke(task);
            }

            task.State = DownloadState.Completed;
            task.ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadState.Cancelled;
        }
        catch (Exception ex)
        {
            task.State = DownloadState.Failed;
            task.ErrorMessage = ex.Message;
        }
        finally
        {
            _activeTasks.TryRemove(task.Url, out _);
            DownloadCompleted?.Invoke(task);
        }
    }

    public List<ModelInfo> GetModelsForService(ServiceDefinition service, string installDir)
    {
        // Ollama doesn't lay models out as files we can scan — they're
        // SHA-named blobs in %USERPROFILE%\.ollama\models\blobs\ keyed by
        // a manifest. Use HTTP /api/tags via GetOllamaModelsAsync instead.
        // Returning empty here means the sync UI path (legacy) shows
        // nothing for Ollama; ServiceViewModel.RefreshModels has the
        // async branch that actually populates the list.
        if (string.IsNullOrEmpty(service.ModelsSubfolder))
            return new();

        // Prefer ModelsScanRoot when set (parent dir holding multiple
        // model-type subfolders, e.g. ComfyUI's "models" → checkpoints/,
        // loras/, vae/, …). Fall back to ModelsSubfolder so services that
        // keep a flat layout still work without a registry change.
        string scanRel = string.IsNullOrEmpty(service.ModelsScanRoot)
            ? service.ModelsSubfolder
            : service.ModelsScanRoot;

        string modelsDir = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? Path.Combine(installDir, scanRel)
            : Path.Combine(installDir, service.RelativeInstallPath, scanRel);

        if (!Directory.Exists(modelsDir))
            return new();

        var files = ModelExtensions
            .SelectMany(pattern => Directory.GetFiles(modelsDir, pattern, SearchOption.AllDirectories))
            .Distinct()
            .Select(path => new ModelInfo
            {
                ServiceId = service.Id,
                FileName = Path.GetFileName(path),
                FullPath = path,
                SizeBytes = new FileInfo(path).Length
            })
            .ToList();

        return files;
    }

    // Ollama-specific model listing: hits the local API instead of
    // scanning the filesystem. Returns empty on any failure (Ollama not
    // running, timeout, parse error) — caller should treat that as "no
    // models known" rather than escalate.
    public async Task<List<ModelInfo>> GetOllamaModelsAsync(int port = 11434)
    {
        try
        {
            using var resp = await _ollamaApiClient.GetAsync($"http://127.0.0.1:{port}/api/tags").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var result = new List<ModelInfo>();
            if (!doc.RootElement.TryGetProperty("models", out var models)) return result;
            foreach (var m in models.EnumerateArray())
            {
                string name = m.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? "?"
                    : "?";
                long size = m.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number
                    ? sizeProp.GetInt64()
                    : 0;
                result.Add(new ModelInfo
                {
                    ServiceId = "ollama",
                    FileName = name,
                    // FullPath empty on purpose: Ollama models are
                    // SHA-named blobs in %USERPROFILE%\.ollama\models\blobs\
                    // keyed by a manifest, not a single file we can
                    // unlink. Deletion goes through DeleteOllamaModelAsync
                    // → DELETE /api/delete; ServiceViewModel.DeleteModel
                    // detects the empty FullPath + ollama service id and
                    // routes there.
                    FullPath = "",
                    SizeBytes = size,
                });
            }
            return result;
        }
        catch
        {
            return new();
        }
    }

    // Ollama deletion via its HTTP API. Uses an explicit DELETE with a
    // JSON body `{"name": "<tag>"}` — Ollama returns 200 on success, 404 on
    // unknown name, 5xx on internal errors. Returns true only on 2xx so the
    // UI can leave the row in place if the daemon refused. Uses a 10s
    // per-call budget (vs the 2s polling client) — `ollama rm` walks the
    // manifest and unlinks blobs, which can take a couple seconds on
    // sluggish disks.
    public async Task<bool> DeleteOllamaModelAsync(string modelName, int port = 11434, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var req = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{port}/api/delete")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { name = modelName }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            // Bypass _ollamaApiClient's 2s default — this call legitimately
            // runs longer than a /api/tags poll.
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var task in _activeTasks.Values)
            task.CancellationTokenSource.Cancel();
        _httpClient.Dispose();
        _ollamaApiClient.Dispose();
    }
}
