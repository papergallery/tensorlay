using System.Collections.Concurrent;
using System.Net.Http;

namespace TensorLay.Services;

public class HealthCheckService : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, string> _endpoints = new();
    private readonly Timer _timer;
    private bool _disposed;

    public event Action<string, bool>? HealthChanged;

    public HealthCheckService()
    {
        _timer = new Timer(_ => CheckAll(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void RegisterService(string serviceId, string baseUrl, string healthEndpoint)
    {
        _endpoints[serviceId] = baseUrl.TrimEnd('/') + healthEndpoint;
    }

    public void UnregisterService(string serviceId)
    {
        _endpoints.TryRemove(serviceId, out _);
    }

    private void CheckAll()
    {
        foreach (var kv in _endpoints.ToArray())
            _ = CheckHealthAsync(kv.Key, kv.Value);
    }

    private async Task CheckHealthAsync(string serviceId, string url)
    {
        bool isHealthy = false;
        try
        {
            var response = await _httpClient.GetAsync(url);
            isHealthy = (int)response.StatusCode == 200;
        }
        catch
        {
            // Connection refused, timeout, etc.
        }
        HealthChanged?.Invoke(serviceId, isHealthy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _httpClient.Dispose();
    }
}
