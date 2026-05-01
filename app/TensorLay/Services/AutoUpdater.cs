using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TensorLay.Services;

public class AutoUpdater : IDisposable
{
    private const string UpdateUrl = "https://tensorlay.com/updates/version.json";
    private const string ExeUrl = "https://tensorlay.com/updates/GpuHub.exe";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private bool _disposed;

    // Cached between CheckForUpdate and DownloadAndApply so the integrity
    // check can compare against the manifest the user just consented to.
    private string? _pendingSha256;

    public event Action<string>? UpdateLog;
    public event Action<double>? DownloadProgress;

    public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<(bool available, string? newVersion)> CheckForUpdate()
    {
        try
        {
            var json = await _httpClient.GetStringAsync(UpdateUrl);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (info == null) return (false, null);

            var current = new Version(CurrentVersion);
            var remote = new Version(info.Version);

            if (remote > current)
            {
                _pendingSha256 = info.Sha256;
                UpdateLog?.Invoke($"Update available: {CurrentVersion} -> {info.Version}");
                return (true, info.Version);
            }

            UpdateLog?.Invoke($"Up to date: {CurrentVersion}");
            return (false, null);
        }
        catch (Exception ex)
        {
            UpdateLog?.Invoke($"Update check failed: {ex.Message}");
            return (false, null);
        }
    }

    public string? LastError { get; private set; }

    public async Task<bool> DownloadAndApply()
    {
        LastError = null;
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                LastError = "Cannot determine exe path";
                return false;
            }

            // Download to temp folder (avoids permission issues on Desktop/Program Files)
            var tempPath = Path.Combine(Path.GetTempPath(), "TensorLay_update.exe");
            var backupPath = currentExe + ".backup";

            UpdateLog?.Invoke("Downloading update...");

            using var response = await _httpClient.GetAsync(ExeUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloaded += bytesRead;
                    if (totalBytes > 0)
                        DownloadProgress?.Invoke((double)downloaded / totalBytes * 100);
                }
            }

            // Integrity check: TLS protects transit, but server compromise or
            // CDN cache poisoning could still serve a tampered binary. Refuse
            // to install anything whose hash doesn't match the manifest.
            if (!await VerifySha256(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return false;
            }

            UpdateLog?.Invoke("Applying update...");

            int callerPid = Environment.ProcessId;
            var scriptPath = Path.Combine(Path.GetTempPath(), "tensorlay_update.ps1");

            // Escape single quotes for PowerShell single-quoted string literals
            // (e.g. user "O'Brien" → C:\Users\O'Brien\... would otherwise break the script)
            static string Esc(string s) => s.Replace("'", "''");

            // Note: $$"""...""" raw-string interpolation — single { and } are
            // literal so PowerShell script blocks don't need escaping.
            var script = $$"""
                $callerPid = {{callerPid}}
                $current = '{{Esc(currentExe)}}'
                $backup  = '{{Esc(backupPath)}}'
                $temp    = '{{Esc(tempPath)}}'

                # Wait for the launching process to fully exit so its file
                # handle on $current is released before we try to overwrite it.
                # Timeout caps the wait so a stuck process can't hang updates.
                try { Wait-Process -Id $callerPid -Timeout 30 -ErrorAction Stop } catch { }

                # Move can still briefly fail if AV/Indexer holds a handle —
                # retry a few times instead of leaving the install half-done.
                $moved = $false
                for ($i = 0; $i -lt 5; $i++) {
                    try {
                        Move-Item -Path $current -Destination $backup -Force -ErrorAction Stop
                        Move-Item -Path $temp    -Destination $current -Force -ErrorAction Stop
                        $moved = $true
                        break
                    } catch {
                        Start-Sleep -Seconds 1
                    }
                }

                if (-not $moved) {
                    # Roll back if we managed to move the old exe aside but
                    # couldn't put the new one in place — otherwise the user
                    # is left with no installed binary at all.
                    if ((Test-Path $backup) -and (-not (Test-Path $current))) {
                        Move-Item -Path $backup -Destination $current -Force -ErrorAction SilentlyContinue
                    }
                    exit 1
                }

                # Launch via Shell.Application so the new exe runs with the
                # interactive user's medium IL token instead of inheriting
                # this script's elevated token. Without this, every update
                # would silently leave TensorLay running as administrator.
                $shell = New-Object -ComObject Shell.Application
                $shell.ShellExecute($current)

                Remove-Item -Path $MyInvocation.MyCommand.Path -Force
                """;
            File.WriteAllText(scriptPath, script, Encoding.UTF8);

            // Launch elevated (UAC prompt) so it can write to Program Files
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Esc(scriptPath)}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            UpdateLog?.Invoke("Restarting...");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            UpdateLog?.Invoke($"Update failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> VerifySha256(string filePath)
    {
        if (string.IsNullOrEmpty(_pendingSha256))
        {
            // Fail closed. publish.sh always writes sha256 for v0.8.0+
            // releases — a missing field means either a stripped manifest
            // (network attacker editing version.json over plain HTTP, e.g.
            // the bare-IP fallback used by some legacy clients) or a server
            // misconfiguration. Either way, refusing to install is safer
            // than running an unverified 162 MB binary.
            LastError = "Manifest missing sha256 — refusing to install unverified binary";
            UpdateLog?.Invoke(LastError);
            return false;
        }

        try
        {
            using var sha = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await sha.ComputeHashAsync(stream);
            var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var expected = _pendingSha256.Trim().ToLowerInvariant();

            if (actual != expected)
            {
                LastError = "Integrity check failed: hash mismatch";
                UpdateLog?.Invoke($"SHA256 mismatch — expected {expected[..16]}…, got {actual[..16]}…");
                return false;
            }

            UpdateLog?.Invoke("SHA256 verified.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"SHA256 verification error: {ex.Message}";
            UpdateLog?.Invoke(LastError);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
