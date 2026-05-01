using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TensorLay.Services;

public class AccountService
{
    private const string ApiBase = "https://tensorlay.com";

    private static readonly string AuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TensorLay",
        "auth.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public AccountSession? Cached { get; private set; }

    public AccountSession? LoadCached()
    {
        if (!File.Exists(AuthPath)) return null;
        try
        {
            byte[] fileBytes = File.ReadAllBytes(AuthPath);
            bool wasLegacyPlaintext = false;
            string json;

            try
            {
                // Current format: DPAPI-encrypted JSON. Tied to the user
                // account on this machine — copying auth.json to another
                // PC or even another Windows user yields a decrypt error.
                byte[] decrypted = ProtectedData.Unprotect(
                    fileBytes, null, DataProtectionScope.CurrentUser);
                json = Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Legacy format from pre-0.8.0: raw plaintext JSON. Read
                // it as-is and rewrite encrypted below so the next launch
                // hits the encrypted path.
                json = Encoding.UTF8.GetString(fileBytes);
                wasLegacyPlaintext = true;
            }

            Cached = JsonSerializer.Deserialize<AccountSession>(json, JsonOpts);

            if (Cached != null && wasLegacyPlaintext)
                Save(Cached);

            return Cached;
        }
        catch
        {
            return null;
        }
    }

    public async Task<AccountSession> SignInWithTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Empty token", nameof(token));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/api/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sign-in rejected by server: HTTP {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);
        var meResp = JsonSerializer.Deserialize<MeResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Malformed /api/me response");

        var session = new AccountSession
        {
            Token = token,
            UserId = meResp.User.Id,
            Email = meResp.User.Email,
            Username = meResp.User.Username,
            SignedInAt = DateTime.UtcNow,
        };

        Save(session);
        Cached = session;
        return session;
    }

    public void SignOut()
    {
        Cached = null;
        if (File.Exists(AuthPath))
        {
            try { File.Delete(AuthPath); } catch { /* ignore */ }
        }
    }

    private static void Save(AccountSession session)
    {
        string dir = Path.GetDirectoryName(AuthPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(session, JsonOpts);
        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        byte[] encrypted = ProtectedData.Protect(
            plaintext, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AuthPath, encrypted);
    }

    private class MeResponse
    {
        public UserDto User { get; set; } = null!;
    }

    private class UserDto
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string Username { get; set; } = "";
    }
}

public class AccountSession
{
    public string Token { get; set; } = "";
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime SignedInAt { get; set; }
}
