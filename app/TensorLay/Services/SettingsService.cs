using System.IO;
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

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
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
}
