using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorLay.Models;

namespace TensorLay.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TensorLay",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Marker for fields encrypted with DPAPI (CurrentUser scope). Anything
    // that starts with this prefix on disk is treated as ciphertext; anything
    // else is plaintext (legacy data from before encryption was added) and
    // silently re-encrypted on next Save.
    private const string DpapiPrefix = "DPAPI:";

    // Cache the raw JSON, not the parsed object — every Load() deserializes
    // a fresh AppSettings so callers can safely mutate the returned object
    // without leaking changes back into the cache. Disk read happens once
    // per process (or after a Save). MainViewModel + each ServiceViewModel
    // ctor + every Start/Install call hits Load — without this cache that's
    // 10+ disk reads on a cold start.
    private string? _cachedJson;
    private readonly object _lock = new();

    public AppSettings Load()
    {
        string json;
        lock (_lock)
        {
            if (_cachedJson is null)
            {
                if (!File.Exists(SettingsPath))
                {
                    _cachedJson = "{}";
                }
                else
                {
                    try { _cachedJson = File.ReadAllText(SettingsPath); }
                    catch { _cachedJson = "{}"; }
                }
            }
            json = _cachedJson;
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }

        // Decrypt sensitive fields lazily after deserialization. Failure to
        // decrypt (e.g. user-profile change, settings.json copied from another
        // machine) is logged-as-empty rather than thrown — losing the bearer
        // token just means polling pauses until the user re-pairs.
        settings.RemoteTasksToken = TryDecryptField(settings.RemoteTasksToken);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        // Encrypt sensitive fields before serializing. Mutating the passed-in
        // object's field is intentional — Load() returns a fresh copy each
        // call (we re-deserialize from cached JSON), and the caller in turn
        // doesn't reuse the same AppSettings instance after Save.
        settings.RemoteTasksToken = EncryptField(settings.RemoteTasksToken);

        string dir = Path.GetDirectoryName(SettingsPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        // Atomic replace: write to a sibling file then rename. Without this,
        // a crash mid-write produces a truncated settings.json which the
        // next Load() falls back to defaults — silently dropping VPS host,
        // SSH key path, and pairing state.
        string tmpPath = SettingsPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, SettingsPath, overwrite: true);

        lock (_lock)
        {
            _cachedJson = json;
        }
    }

    // Encrypts plaintext via DPAPI CurrentUser scope. Empty string maps to
    // empty string (no point encrypting "no token"). Already-encrypted
    // values pass through — defensive in case Save() is called twice
    // without an intervening Load() that decrypts first.
    private static string EncryptField(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (plaintext.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return plaintext;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipher = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            // DPAPI failure is unusual on Windows — fall back to plaintext
            // rather than dropping the value entirely. Caller doesn't know
            // about encryption status and shouldn't have to.
            return plaintext;
        }
    }

    private static string TryDecryptField(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (!raw.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            // Legacy plaintext from a pre-DPAPI build — surface as-is; the
            // next Save() re-encrypts it. Same upgrade pattern as
            // AccountService.
            return raw;
        }
        try
        {
            byte[] cipher = Convert.FromBase64String(raw.Substring(DpapiPrefix.Length));
            byte[] bytes = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Wrong user, corrupted cipher, machine moved — treat as
            // unrecoverable and force re-pair by returning empty.
            return "";
        }
    }
}
