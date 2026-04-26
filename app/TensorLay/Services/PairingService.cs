using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TensorLay.Services;

public class PairingResult
{
    public bool Success { get; set; }
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string Error { get; set; } = "";
}

public class PairingService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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
            var body = JsonSerializer.Serialize(new
            {
                code = pairingCode.Trim().ToUpper(),
                ssh_public_key = sshPublicKey.Trim()
            });

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
