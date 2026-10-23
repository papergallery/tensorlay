using System.Diagnostics;
using System.Runtime.InteropServices;
using TensorLay.Models;

namespace TensorLay.Services;

// Coarse generation buckets, enough to drive CUDA-wheel decisions. We don't
// distinguish individual SKUs (5070 vs 5090) because the toolkit/driver
// constraints are per-architecture, not per-SKU.
public enum GpuGeneration
{
    Unknown,
    Pre30,    // GTX 10/16 series, RTX 20 series — sm_75 and below
    Rtx30,    // Ampere, sm_86
    Rtx40,    // Ada Lovelace, sm_89
    Rtx50,    // Blackwell, sm_120 — needs CUDA 12.6+ / torch 2.6+
}

public class GpuMonitor : IDisposable
{
    private readonly Timer _timer;
    // Guards against overlapping polls if a previous nvidia-smi call is
    // still running when the next tick fires.
    private int _polling;
    private bool _disposed;

    public GpuInfo? CurrentInfo { get; private set; }

    public event Action<GpuInfo>? GpuInfoUpdated;

    public GpuMonitor()
    {
        _timer = new Timer(
            _ =>
            {
                if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
                _ = PollWithFlagAsync();
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3));
    }

    // try/finally guarantees _polling is reset even when PollAsync throws
    // synchronously before its first await (e.g. ProcessStartInfo construction
    // fails). The previous ContinueWith(...) version had a small window where
    // a synchronous fault could escape and leave _polling stuck at 1.
    private async Task PollWithFlagAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) await PollMacAsync().ConfigureAwait(false);
            else await PollAsync().ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private async Task PollAsync()
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string output;
            try
            {
                // ReadToEndAsync + WaitForExitAsync both honour the token, so
                // a hung nvidia-smi can't pile up timer threads — we kill it
                // and move on to the next tick.
                var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var exitTask = process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(readTask, exitTask).ConfigureAwait(false);
                output = (await readTask).Trim();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }

            var info = Parse(output);
            if (info is not null)
            {
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

    // wmic.exe is deprecated and removed by default on fresh Windows 11 24H2
    // installs. GlobalMemoryStatusEx is in kernel32 and has been there since
    // Windows 2000 — never going away.
    private static void GetSystemRam(GpuInfo info)
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
            {
                info.RamTotalMb = (int)(status.ullTotalPhys / (1024UL * 1024UL));
                info.RamUsedMb = (int)((status.ullTotalPhys - status.ullAvailPhys) / (1024UL * 1024UL));
            }
        }
        catch { /* best-effort: leave RAM fields at 0 if the API call fails */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    // One-shot generation detection for install-time decisions. Cheaper than
    // waiting for the polling loop's first tick (caller often runs before
    // the 3s timer has fired). Returns Unknown on any failure — callers
    // should treat that as "skip the gen-specific tweak", not as an error.
    public static async Task<GpuGeneration> DetectGenerationAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return GpuGeneration.Unknown;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                await Task.WhenAll(readTask, process.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
                string name = (await readTask).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
                return ClassifyGeneration(name);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return GpuGeneration.Unknown;
            }
        }
        catch
        {
            return GpuGeneration.Unknown;
        }
    }

    // Coarse name-based classification. nvidia-smi reports e.g. "NVIDIA
    // GeForce RTX 4080" / "NVIDIA GeForce RTX 5090". We only need the
    // architecture bucket, not the SKU — and we deliberately bias toward
    // Unknown for ambiguous strings so the caller doesn't make a worse
    // decision than skipping the optimization.
    internal static GpuGeneration ClassifyGeneration(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return GpuGeneration.Unknown;
        string n = name.ToUpperInvariant();

        // Match the digit immediately after "RTX " — 50/40/30 series.
        // Workstation cards (RTX A6000, RTX 6000 Ada, RTX PRO 6000 Blackwell)
        // are intentionally not classified here — driver story differs and
        // we'd rather skip the warning than misfire it on a Quadro.
        var m = System.Text.RegularExpressions.Regex.Match(n, @"\bRTX\s+(\d{4})\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int model))
        {
            if (model >= 5000 && model < 6000) return GpuGeneration.Rtx50;
            if (model >= 4000 && model < 5000) return GpuGeneration.Rtx40;
            if (model >= 3000 && model < 4000) return GpuGeneration.Rtx30;
            if (model >= 2000 && model < 3000) return GpuGeneration.Pre30;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\bGTX\s+1\d{3}\b"))
            return GpuGeneration.Pre30;

        return GpuGeneration.Unknown;
    }

    // ── macOS (Apple Silicon) ───────────────────────────────────────────────
    // No nvidia-smi and no separate VRAM: the GPU shares system memory. All
    // three sources below work without sudo (powermetrics would add power
    // and temperature but needs root, so those stay 0 and the UI hides them):
    //   sysctl  machdep.cpu.brand_string -> "Apple M2"     (name, once)
    //           hw.memsize               -> total RAM
    //   vm_stat                          -> used RAM (active+wired+compressed)
    //   ioreg   IOAccelerator PerformanceStatistics
    //           "Device Utilization %"   -> GPU load
    //           "In use system memory"   -> memory the GPU currently holds
    // VramTotalMb reports the share of RAM Metal lets the GPU wire (about 2/3
    // of RAM up to 36 GB, 3/4 above), which is the real ceiling for models.
    private string? _macGpuName;
    private long _macRamBytes;

    private async Task PollMacAsync()
    {
        try
        {
            _macGpuName ??= (await RunCaptureAsync("/usr/sbin/sysctl", "-n machdep.cpu.brand_string").ConfigureAwait(false))?.Trim();
            if (_macRamBytes == 0 &&
                long.TryParse((await RunCaptureAsync("/usr/sbin/sysctl", "-n hw.memsize").ConfigureAwait(false))?.Trim(), out long mem))
                _macRamBytes = mem;

            var info = new GpuInfo { GpuName = string.IsNullOrEmpty(_macGpuName) ? "Apple GPU" : _macGpuName };
            if (_macRamBytes > 0)
            {
                info.RamTotalMb = (int)(_macRamBytes / 1_048_576);
                double share = _macRamBytes > 36L * 1024 * 1024 * 1024 ? 0.75 : 2.0 / 3.0;
                info.VramTotalMb = (int)(info.RamTotalMb * share);
            }

            string? vm = await RunCaptureAsync("/usr/bin/vm_stat", "").ConfigureAwait(false);
            if (vm is not null) info.RamUsedMb = ParseVmStatUsedMb(vm);

            string? io = await RunCaptureAsync("/usr/sbin/ioreg", "-r -d 1 -c IOAccelerator").ConfigureAwait(false);
            if (io is not null)
            {
                var util = System.Text.RegularExpressions.Regex.Match(io, "\"Device Utilization %\"=(\\d+)");
                if (util.Success) info.GpuUtilPercent = float.Parse(util.Groups[1].Value);
                var inUse = System.Text.RegularExpressions.Regex.Match(io, "\"In use system memory\"=(\\d+)");
                if (inUse.Success && long.TryParse(inUse.Groups[1].Value, out long bytes))
                    info.VramUsedMb = (int)(bytes / 1_048_576);
            }

            CurrentInfo = info;
            GpuInfoUpdated?.Invoke(info);
        }
        catch
        {
            // Any probe missing — skip this tick, same as the nvidia-smi path.
        }
    }

    // vm_stat prints "Mach Virtual Memory Statistics: (page size of 16384 bytes)"
    // then "Pages active:   123456." lines. Used = active + wired + compressed,
    // which is what Activity Monitor calls "Memory Used".
    internal static int ParseVmStatUsedMb(string vmStat)
    {
        long pageSize = 16384;
        var ps = System.Text.RegularExpressions.Regex.Match(vmStat, @"page size of (\d+) bytes");
        if (ps.Success) pageSize = long.Parse(ps.Groups[1].Value);

        long Pages(string label)
        {
            var m = System.Text.RegularExpressions.Regex.Match(vmStat, label + @":\s+(\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : 0;
        }

        long used = Pages("Pages active") + Pages("Pages wired down") + Pages("Pages occupied by compressor");
        return (int)(used * pageSize / 1_048_576);
    }

    private static async Task<string?> RunCaptureAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await Task.WhenAll(readTask, process.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
