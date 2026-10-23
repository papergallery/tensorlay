using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TensorLay.Services;

public class PairingResult
{
    public bool Success { get; set; }
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string ServiceToken { get; set; } = "";
    public List<string> HostKeyFingerprints { get; set; } = new();
    // v0.9.0 — bearer for /api/tasks/* (remote install requests). Empty when
    // pairing against a v1.2.x relay; the desktop then leaves the feature
    // disabled and shows a hint in Settings ("update your relay").
    public string RemoteTasksToken { get; set; } = "";
    public string Error { get; set; } = "";
}

public class PairingService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    // SnakeCaseLower handles both directions: the request body's
    // PascalCase anonymous-type fields serialize to snake_case JSON
    // (matching FastAPI's pydantic model field names), and incoming
    // snake_case JSON deserializes back into PascalCase PairingResult
    // properties. Without this, the previous version silently dropped
    // ssh_user / ssh_port from the response (relay returned them but
    // they never made it into PairingResult — the SSH user came back
    // as an empty string and SSH connect would fail).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<bool> CheckRelayHealthAsync(string vpsHost, CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await _httpClient.GetAsync($"http://{vpsHost}:8090/health", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PairingResult> PairAsync(string vpsHost, string pairingCode, string sshPublicKey, CancellationToken cancellationToken = default)
    {
        try
        {
            // Anonymous-type field names in PascalCase — JsonOpts converts to
            // snake_case on the wire (Code → "code", SshPublicKey → "ssh_public_key").
            var body = JsonSerializer.Serialize(new
            {
                Code = pairingCode.Trim().ToUpper(),
                SshPublicKey = sshPublicKey.Trim(),
            }, JsonOpts);

            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync($"http://{vpsHost}:8090/pair", content, cancellationToken);

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<PairingResult>(json, JsonOpts);

            if (result != null)
                return result;

            return new PairingResult { Success = false, Error = "Empty response from server" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new PairingResult { Success = false, Error = $"Connection failed: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new PairingResult { Success = false, Error = "Connection timed out" };
        }
        catch (Exception ex)
        {
            return new PairingResult { Success = false, Error = ex.Message };
        }
    }
}
