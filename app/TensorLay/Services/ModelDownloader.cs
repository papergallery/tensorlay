using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
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

    private readonly HttpClient _httpClient = new();
    // Separate, short-timeout client for Ollama /api/tags polling. Default
    // HttpClient.Timeout (100s) would freeze the Models tab if Ollama is
    // unreachable — 2s is plenty for a localhost call.
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

        try
        {
            string? dir = Path.GetDirectoryName(task.TargetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var response = await _httpClient.GetAsync(
                task.Url,
                HttpCompletionOption.ResponseHeadersRead,
                task.CancellationTokenSource.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            task.TotalBytes = response.Content.Headers.ContentLength ?? 0;

            await using var stream = await response.Content.ReadAsStreamAsync(task.CancellationTokenSource.Token);
            await using var file = File.Create(task.TargetPath);

            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, task.CancellationTokenSource.Token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, bytesRead), task.CancellationTokenSource.Token);
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
                    // FullPath empty on purpose: Ollama models aren't a
                    // single file at a fixed path — DeleteModel falls
                    // back to a no-op for Ollama until we wire `ollama
                    // rm` / DELETE /api/delete (TODO v0.9.6+).
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
