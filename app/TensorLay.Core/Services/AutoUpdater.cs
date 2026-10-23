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
    // Windows and macOS publish independent manifests: the Mac build ships
    // as a zipped .app under /updates/mac/ with its own version + sha256, so
    // a Windows release never offers itself to a Mac and vice versa.
    private static readonly string UpdateUrl = OperatingSystem.IsMacOS()
        ? "https://tensorlay.com/updates/mac/version.json"
        : "https://tensorlay.com/updates/version.json";
    private static readonly string ExeUrl = OperatingSystem.IsMacOS()
        ? "https://tensorlay.com/updates/mac/TensorLay-mac.zip"
        : "https://tensorlay.com/updates/GpuHub.exe";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private bool _disposed;

    // Cached between CheckForUpdate and DownloadAndApply so the integrity
    // check can compare against the manifest the user just consented to.
    private string? _pendingSha256;

    public event Action<string>? UpdateLog;
    public event Action<double>? DownloadProgress;

    // Entry assembly = the front-end exe that carries the release version
    // (this class lives in TensorLay.Core).
    public string CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

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
            var tempPath = Path.Combine(Path.GetTempPath(),
                OperatingSystem.IsMacOS() ? "TensorLay_update.zip" : "TensorLay_update.exe");
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

            if (OperatingSystem.IsMacOS())
                return await ApplyMacUpdate(currentExe, tempPath);

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

            // Resolve full path to powershell.exe — ShellExecute with
            // Verb="runas" can't always find it via PATH, and on some Win11
            // setups CWD inheritance fails with "system cannot find the file
            // specified" if the calling process's working directory is gone.
            // Falling back to pwsh.exe (PowerShell 7) covers minimal Win11
            // images that ship without Windows PowerShell.
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string winPs = Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe");
            string psExe = File.Exists(winPs) ? winPs : "pwsh.exe";

            Process.Start(new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Esc(scriptPath)}\"",
                WorkingDirectory = Path.GetTempPath(),
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

    // macOS: the payload is a zipped TensorLay.app. Unpack it next to the
    // download with ditto (keeps the code signature, symlinks and exec bits
    // that a plain unzip can mangle), then hand off to a detached sh script
    // that waits for this process to exit, swaps the bundle in place, and
    // relaunches it via `open`. Files fetched by HttpClient carry no
    // quarantine flag, so the new bundle opens without a Gatekeeper prompt.
    private async Task<bool> ApplyMacUpdate(string currentExe, string zipPath)
    {
        // .../TensorLay.app/Contents/MacOS/TensorLay -> .../TensorLay.app
        string? bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(currentExe)));
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Not running from an .app bundle — download the new version from tensorlay.com instead.";
            return false;
        }
        string? parent = Path.GetDirectoryName(bundle);
        if (parent is null || !IsWritableDirectory(parent))
        {
            LastError = $"No write access to {parent} — move TensorLay to your Applications folder (or reinstall it) and try again.";
            return false;
        }

        string stage = Path.Combine(Path.GetTempPath(), "TensorLay_update_stage");
        try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
        Directory.CreateDirectory(stage);

        var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto")
        {
            ArgumentList = { "-x", "-k", zipPath, stage },
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        if (ditto is null)
        {
            LastError = "Could not run ditto to unpack the update.";
            return false;
        }
        string dittoErr = await ditto.StandardError.ReadToEndAsync();
        await ditto.WaitForExitAsync();
        string newBundle = Path.Combine(stage, "TensorLay.app");
        if (ditto.ExitCode != 0 || !Directory.Exists(newBundle))
        {
            LastError = $"Unpacking the update failed: {dittoErr.Trim()}";
            return false;
        }

        // Single-quote for sh; a ' inside the path becomes '\''.
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        string backup = bundle + ".previous";
        string script = $$"""
            #!/bin/sh
            # Wait (max ~30 s) for the running TensorLay to exit.
            i=0
            while kill -0 {{Environment.ProcessId}} 2>/dev/null && [ $i -lt 60 ]; do sleep 0.5; i=$((i+1)); done
            rm -rf {{Q(backup)}}
            if mv {{Q(bundle)}} {{Q(backup)}} && mv {{Q(newBundle)}} {{Q(bundle)}}; then
              rm -rf {{Q(backup)}}
            else
              # Roll back so the user is never left without an app.
              [ -d {{Q(bundle)}} ] || mv {{Q(backup)}} {{Q(bundle)}}
            fi
            rm -f {{Q(zipPath)}}
            open {{Q(bundle)}}
            rm -f "$0"
            """;
        string scriptPath = Path.Combine(Path.GetTempPath(), "tensorlay_update.sh");
        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));

        // nohup + own session: the script must outlive this process.
        Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"nohup /bin/sh {Q(scriptPath)} >/dev/null 2>&1 &" },
            UseShellExecute = false,
        });

        UpdateLog?.Invoke("Restarting...");
        return true;
    }

    private static bool IsWritableDirectory(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".tensorlay-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
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
