using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

// Remote model cleanup (relay v1.6.0+). While the user has
// Settings.AllowRemoteModelCleanup on, this service
//   - pushes a model inventory to PUT /api/inventory so the VPS agent can
//     see what's on disk (rescanned every couple of minutes, uploaded when
//     it changes and at least every 15 minutes as a liveness signal);
//   - polls GET /api/deletions/pending and surfaces new requests through
//     DeletionReceived for the approval window. Nothing is deleted without
//     the user's click there.
// With the toggle off, pending requests are auto-rejected with a reason so
// the agent doesn't wait, and no inventory is sent.
//
// Runs unconditionally like RemoteLogService — the toggle is checked on
// every poll, so flipping it in Settings takes effect without a restart.
public class RemoteCleanupService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InventoryScanInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InventoryHeartbeat = TimeSpan.FromMinutes(15);
    // Relays older than 1.6.0 answer 404; don't knock every 30 s.
    private static readonly TimeSpan UnsupportedRelayRetry = TimeSpan.FromMinutes(10);

    private const int MaxItemErrorLength = 300;
    private const int MaxErrorMsgLength = 500;

    private static readonly JsonSerializerOptions InventoryJson = new() { WriteIndented = false };

    private readonly SettingsService _settingsService;
    private readonly ModelDownloader _modelDownloader;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };

    private readonly ConcurrentDictionary<string, byte> _seenIds = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    private volatile bool _inventoryDirty = true;
    private DateTime _lastInventoryScanUtc = DateTime.MinValue;
    private DateTime _lastInventoryUploadUtc = DateTime.MinValue;
    private string? _lastInventoryHash;
    private DateTime _unsupportedUntilUtc = DateTime.MinValue;
    private bool _loggedUnsupported;

    public RemoteCleanupService(SettingsService settingsService, ModelDownloader modelDownloader)
    {
        _settingsService = settingsService;
        _modelDownloader = modelDownloader;
        // A finished download changes the inventory; don't wait for the
        // periodic rescan to tell the agent.
        _modelDownloader.DownloadCompleted += _ => MarkInventoryDirty();
    }

    // Fired on a background thread for each new pending request while the
    // toggle is on. Subscribers marshal to the UI thread.
    public event Action<RemoteDeletionRequest>? DeletionReceived;

    public event Action<string>? Log;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void StartPolling()
    {
        if (_disposed || IsRunning) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoop(_cts.Token));
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

    // Force a rescan + upload on the next poll — called after anything
    // deletes or adds model files so the agent sees it within 30 s.
    public void MarkInventoryDirty() => _inventoryDirty = true;

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
        if (DateTime.UtcNow < _unsupportedUntilUtc) return;

        string baseUrl = BaseUrl(settings);

        if (settings.AllowRemoteModelCleanup)
            await MaybePushInventoryAsync(settings, baseUrl, ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/deletions/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            MarkRelayUnsupported();
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode is 401 or 403)
                Log?.Invoke($"Auth rejected ({(int)resp.StatusCode}); re-pair to refresh token.");
            return;
        }

        RemoteDeletionListResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<RemoteDeletionListResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            Log?.Invoke($"Pending poll: bad JSON: {ex.Message}");
            return;
        }
        if (body is null) return;

        foreach (var request in body.Requests)
        {
            if (string.IsNullOrEmpty(request.Id) || request.State != "pending") continue;
            if (!_seenIds.TryAdd(request.Id, 0)) continue;

            string handle = request.Id[..Math.Min(8, request.Id.Length)];
            if (!settings.AllowRemoteModelCleanup)
            {
                _ = PostResultAsync(request.Id, new RemoteDeletionResult
                {
                    State = "rejected",
                    ErrorMsg = "Remote model cleanup is OFF - enable it in TensorLay Settings.",
                }, ct);
                Log?.Invoke($"Auto-rejected deletion request {handle} (toggle off)");
                continue;
            }

            Log?.Invoke($"Deletion request {handle}: {request.Items.Count} item(s) awaiting approval");
            DeletionReceived?.Invoke(request);
        }
    }

    // Reports the outcome of a request. Retries network/5xx failures a few
    // times — by now the files are already gone, so the report is worth
    // some persistence. 4xx (already expired/revoked) is final.
    public async Task<bool> PostResultAsync(string requestId, RemoteDeletionResult result, CancellationToken ct = default)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.RemoteTasksToken)) return false;

        if (result.ErrorMsg is { Length: > MaxErrorMsgLength })
            result.ErrorMsg = result.ErrorMsg[..MaxErrorMsgLength];
        foreach (var item in result.Results)
        {
            if (item.Error is { Length: > MaxItemErrorLength })
                item.Error = item.Error[..MaxItemErrorLength];
        }

        string url = $"{BaseUrl(settings)}/api/deletions/{Uri.EscapeDataString(requestId)}/result";
        string handle = requestId[..Math.Min(8, requestId.Length)];
        int[] retryDelaysSec = { 2, 5, 15 };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(result) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;

                int code = (int)resp.StatusCode;
                if (code is >= 400 and < 500)
                {
                    string detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    Log?.Invoke($"Deletion result for {handle} refused ({code}): {detail}");
                    return false;
                }
                Log?.Invoke($"Deletion result for {handle}: HTTP {code}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Deletion result for {handle}: {ex.Message}");
            }

            if (attempt >= retryDelaysSec.Length) return false;
            try { await Task.Delay(TimeSpan.FromSeconds(retryDelaysSec[attempt]), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task MaybePushInventoryAsync(AppSettings settings, string baseUrl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        bool heartbeatDue = now - _lastInventoryUploadUtc >= InventoryHeartbeat;
        if (!_inventoryDirty && !heartbeatDue && now - _lastInventoryScanUtc < InventoryScanInterval)
            return;

        _inventoryDirty = false;
        _lastInventoryScanUtc = now;

        ModelInventory inventory;
        try
        {
            inventory = await BuildInventoryAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Inventory scan failed: {ex.Message}");
            return;
        }

        // Hash only the model lists: free disk space moves with every write
        // on the drive and would otherwise force an upload on every scan.
        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inventory.Services, InventoryJson))));
        if (hash == _lastInventoryHash && !heartbeatDue)
            return;

        using var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/inventory")
        {
            Content = JsonContent.Create(inventory, options: InventoryJson),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RemoteTasksToken);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                MarkRelayUnsupported();
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _inventoryDirty = true;
                Log?.Invoke($"Inventory upload: HTTP {(int)resp.StatusCode}");
                return;
            }
            _lastInventoryHash = hash;
            _lastInventoryUploadUtc = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _inventoryDirty = true;
            Log?.Invoke($"Inventory upload failed: {ex.Message}");
        }
    }

    public async Task<ModelInventory> BuildInventoryAsync(AppSettings settings)
    {
        string installDir = settings.InstallDirectory;
        var inventory = new ModelInventory
        {
            GeneratedAt = DateTime.UtcNow,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
            InstallDirectory = installDir,
            Disk = ReadDisk(installDir),
        };

        foreach (var def in ServiceRegistry.GetAll())
        {
            if (def.Id == "ollama")
            {
                var models = await _modelDownloader.TryGetOllamaModelsAsync(def.Port).ConfigureAwait(false);
                inventory.Services.Add(ToInventory(def, models, null,
                    models is null ? "Ollama is not running - start it to list its models" : null));
                continue;
            }

            string? root = ModelFiles.GetModelsRoot(def, installDir);
            if (root is null) continue;  // service keeps no models we manage
            bool exists = Directory.Exists(root);
            var files = exists ? await Task.Run(() => ModelFiles.Scan(def, installDir)).ConfigureAwait(false) : null;
            inventory.Services.Add(ToInventory(def, files, root, exists ? null : "not installed"));
        }
        return inventory;
    }

    private static InventoryService ToInventory(ServiceDefinition def, List<ModelInfo>? models, string? root, string? note)
    {
        var files = (models ?? new())
            .OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(m => new InventoryFile { Path = m.RelativePath, SizeBytes = m.SizeBytes, ModifiedAt = m.ModifiedUtc })
            .ToList();
        return new InventoryService
        {
            ServiceId = def.Id,
            DisplayName = def.DisplayName,
            Available = models is not null,
            ModelsRoot = root,
            Note = note,
            TotalBytes = files.Sum(f => f.SizeBytes),
            Files = files,
        };
    }

    private static InventoryDisk? ReadDisk(string installDir)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(Path.GetFullPath(installDir));
            if (string.IsNullOrEmpty(driveRoot)) return null;
            var drive = new DriveInfo(driveRoot);
            return new InventoryDisk { Root = drive.Name, TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace };
        }
        catch
        {
            return null;
        }
    }

    private void MarkRelayUnsupported()
    {
        if (!_loggedUnsupported)
            Log?.Invoke("Relay does not support remote model cleanup yet (needs relay v1.6.0+).");
        _loggedUnsupported = true;
        _unsupportedUntilUtc = DateTime.UtcNow + UnsupportedRelayRetry;
    }

    private static string BaseUrl(AppSettings settings) => $"http://{settings.VpsHost}:8090";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ = StopPolling(); } catch { /* best-effort */ }
        _http.Dispose();
    }
}
