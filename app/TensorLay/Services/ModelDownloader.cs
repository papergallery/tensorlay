using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using TensorLay.Models;

namespace TensorLay.Services;

public class ModelDownloader : IDisposable
{
    private static readonly string[] ModelExtensions = { "*.safetensors", "*.ckpt", "*.gguf" };

    private readonly HttpClient _httpClient = new();
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
        if (string.IsNullOrEmpty(service.ModelsSubfolder))
            return new();

        string modelsDir = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? Path.Combine(installDir, service.ModelsSubfolder)
            : Path.Combine(installDir, service.RelativeInstallPath, service.ModelsSubfolder);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var task in _activeTasks.Values)
            task.CancellationTokenSource.Cancel();
        _httpClient.Dispose();
    }
}
