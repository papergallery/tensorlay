using System.IO;
using TensorLay.Models;

namespace TensorLay.Services;

// One requested deletion after checking it against the real disk: resolved
// path, measured size, and whether it can be deleted at all. The approval
// window shows these, never the agent's raw claims.
public class DeletionCandidate
{
    public RemoteDeletionItem Item { get; init; } = new();
    public string ServiceName { get; init; } = "";
    public bool IsOllama { get; init; }

    // Absolute file path for file-based services; null for Ollama or when
    // the path was rejected.
    public string? FullPath { get; init; }

    public long SizeBytes { get; init; }

    // Null when the item can be deleted; otherwise why not ("not found",
    // "path points outside the models folder", "Ollama is not running").
    public string? Problem { get; init; }

    // Missing files are reported as "missing" rather than "error" — the end
    // state the agent asked for is already true.
    public bool IsMissing { get; init; }

    public bool CanDelete => Problem is null;
}

// Deletes model files and Ollama models on behalf of both the Models page
// (local Delete button) and remote cleanup requests from the relay. All path
// handling goes through ModelFiles so a request can only ever reach model
// files inside a service's models folder.
public class ModelDeletionService
{
    private const string OllamaId = "ollama";

    private readonly ModelDownloader _modelDownloader;
    private readonly SettingsService _settingsService;

    public ModelDeletionService(ModelDownloader modelDownloader, SettingsService settingsService)
    {
        _modelDownloader = modelDownloader;
        _settingsService = settingsService;
    }

    public async Task<List<DeletionCandidate>> PreflightAsync(IEnumerable<RemoteDeletionItem> items)
    {
        var list = items.ToList();
        string installDir = _settingsService.Load().InstallDirectory;

        // Ask Ollama once for the whole batch; null means it isn't reachable.
        Dictionary<string, long>? ollamaSizes = null;
        if (list.Any(i => i.ServiceId == OllamaId))
        {
            var def = FindService(OllamaId);
            var models = await _modelDownloader.TryGetOllamaModelsAsync(def?.Port ?? 11434).ConfigureAwait(false);
            ollamaSizes = models?.GroupBy(m => m.FileName).ToDictionary(g => g.Key, g => g.First().SizeBytes);
        }

        return await Task.Run(() => list.Select(item => Check(item, installDir, ollamaSizes)).ToList()).ConfigureAwait(false);
    }

    private DeletionCandidate Check(RemoteDeletionItem item, string installDir, Dictionary<string, long>? ollamaSizes)
    {
        var def = FindService(item.ServiceId);
        if (def is null)
            return Rejected(item, item.ServiceId, "unknown service");

        if (def.Id == OllamaId)
        {
            if (ollamaSizes is null)
                return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, IsOllama = true, Problem = "Ollama is not running" };
            if (!ollamaSizes.TryGetValue(item.Path, out long size))
                return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, IsOllama = true, Problem = "not found", IsMissing = true };
            return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, IsOllama = true, SizeBytes = size };
        }

        string? fullPath = ModelFiles.ResolveModelPath(def, installDir, item.Path, out string error);
        if (fullPath is null)
            return Rejected(item, def.DisplayName, error);
        if (!File.Exists(fullPath))
            return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, FullPath = fullPath, Problem = "not found", IsMissing = true };
        if (_modelDownloader.IsDownloadingTo(fullPath))
            return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, FullPath = fullPath, Problem = "a download is writing to this file" };

        long bytes;
        try { bytes = new FileInfo(fullPath).Length; }
        catch (Exception ex) { return Rejected(item, def.DisplayName, ex.Message); }
        return new DeletionCandidate { Item = item, ServiceName = def.DisplayName, FullPath = fullPath, SizeBytes = bytes };
    }

    // Permanently deletes one candidate. Deliberately not the Recycle Bin:
    // the point is to free disk space, and multi-GB files overflow the bin
    // anyway (Windows then asks again with a blocking prompt).
    public async Task<RemoteDeletionItemResult> DeleteAsync(DeletionCandidate candidate)
    {
        var result = new RemoteDeletionItemResult
        {
            ServiceId = candidate.Item.ServiceId,
            Path = candidate.Item.Path,
        };

        if (candidate.IsMissing)
        {
            result.Status = "missing";
            return result;
        }
        if (!candidate.CanDelete)
        {
            result.Status = "error";
            result.Error = candidate.Problem;
            return result;
        }

        if (candidate.IsOllama)
        {
            int port = FindService(OllamaId)?.Port ?? 11434;
            if (await _modelDownloader.DeleteOllamaModelAsync(candidate.Item.Path, port).ConfigureAwait(false))
            {
                result.Status = "deleted";
                result.FreedBytes = candidate.SizeBytes;
            }
            else
            {
                result.Status = "error";
                result.Error = "Ollama refused the delete (is it still running?)";
            }
            return result;
        }

        try
        {
            // Re-resolve at delete time: the approval window may have been
            // open for a while — a link could have appeared or a download
            // started since preflight.
            var def = FindService(candidate.Item.ServiceId);
            string error = "unknown service";
            string? path = def is null
                ? null
                : ModelFiles.ResolveModelPath(def, _settingsService.Load().InstallDirectory, candidate.Item.Path, out error);
            if (path is null || path != candidate.FullPath)
            {
                result.Status = "error";
                result.Error = path is null ? error : "the install folder changed since the file was checked";
                return result;
            }
            if (!File.Exists(path))
            {
                result.Status = "missing";
                return result;
            }
            if (_modelDownloader.IsDownloadingTo(path))
            {
                result.Status = "error";
                result.Error = "a download is writing to this file";
                return result;
            }
            long bytes = new FileInfo(path).Length;
            await Task.Run(() => File.Delete(path)).ConfigureAwait(false);
            result.Status = "deleted";
            result.FreedBytes = bytes;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION — typically the
            // service has the model loaded (safetensors are memory-mapped).
            result.Status = "error";
            result.Error = $"file is in use — stop {candidate.ServiceName} and try again";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }
        return result;
    }

    private static DeletionCandidate Rejected(RemoteDeletionItem item, string serviceName, string problem)
        => new() { Item = item, ServiceName = serviceName, Problem = problem };

    private static ServiceDefinition? FindService(string id)
        => ServiceRegistry.GetAll().FirstOrDefault(s => s.Id == id);
}
