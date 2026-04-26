using System.Diagnostics;
using TensorLay.Models;

namespace TensorLay.Services;

public class GpuMonitor : IDisposable
{
    private readonly Timer _timer;
    private bool _disposed;

    public GpuInfo? CurrentInfo { get; private set; }

    public event Action<GpuInfo>? GpuInfoUpdated;

    public GpuMonitor()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    private void Poll()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.used,memory.total,temperature.gpu,utilization.gpu,power.draw,power.limit --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);

            var info = Parse(output);
            if (info is not null)
            {
                // Get system RAM
                GetSystemRam(info);
                CurrentInfo = info;
                GpuInfoUpdated?.Invoke(info);
            }
        }
        catch
        {
            // nvidia-smi not available or failed — silently skip
        }
    }

    private static GpuInfo? Parse(string csvLine)
    {
        if (string.IsNullOrWhiteSpace(csvLine)) return null;

        string[] parts = csvLine.Split(',');
        if (parts.Length < 5) return null;

        return new GpuInfo
        {
            GpuName = parts[0].Trim(),
            VramUsedMb = int.TryParse(parts[1].Trim(), out int used) ? used : 0,
            VramTotalMb = int.TryParse(parts[2].Trim(), out int total) ? total : 0,
            TemperatureCelsius = float.TryParse(parts[3].Trim(), out float temp) ? temp : 0f,
            GpuUtilPercent = float.TryParse(parts[4].Trim(), out float util) ? util : 0f,
            PowerDrawWatts = parts.Length > 5 && float.TryParse(parts[5].Trim(), out float pw) ? pw : 0f,
            PowerLimitWatts = parts.Length > 6 && float.TryParse(parts[6].Trim(), out float pl) ? pl : 0f,
        };
    }

    private static void GetSystemRam(GpuInfo info)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "OS get FreePhysicalMemory,TotalVisibleMemorySize /value",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("TotalVisibleMemorySize=") &&
                    long.TryParse(trimmed.Split('=')[1].Trim(), out long totalKb))
                    info.RamTotalMb = (int)(totalKb / 1024);
                if (trimmed.StartsWith("FreePhysicalMemory=") &&
                    long.TryParse(trimmed.Split('=')[1].Trim(), out long freeKb))
                    info.RamUsedMb = info.RamTotalMb - (int)(freeKb / 1024);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
