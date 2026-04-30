using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TensorLay.Services;

public class AutoUpdater
{
    private const string UpdateUrl = "https://tensorlay.com/updates/version.json";
    private const string ExeUrl = "https://tensorlay.com/updates/GpuHub.exe";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

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

            UpdateLog?.Invoke("Applying update...");

            // PowerShell script with elevated privileges (needed for Program Files)
            var scriptPath = Path.Combine(Path.GetTempPath(), "tensorlay_update.ps1");

            // Escape single quotes for PowerShell single-quoted string literals
            // (e.g. user "O'Brien" → C:\Users\O'Brien\... would otherwise break the script)
            static string Esc(string s) => s.Replace("'", "''");

            var script = $"""
                Start-Sleep -Seconds 2
                $current = '{Esc(currentExe)}'
                $backup  = '{Esc(backupPath)}'
                $temp    = '{Esc(tempPath)}'
                Move-Item -Path $current -Destination $backup -Force -ErrorAction SilentlyContinue
                Move-Item -Path $temp -Destination $current -Force
                Start-Process $current
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

    private class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
