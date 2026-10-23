using System.Diagnostics;
using TensorLay;
using TensorLay.Platform;
using TensorLay.Services;

// tensorlay-cli list
// tensorlay-cli install <id> [--dir DIR]
// tensorlay-cli start <id> [--dir DIR] [--health-timeout SEC]
//     Starts the service, streams its output, waits until the registry's
//     health endpoint answers 2xx, then stops it. Exit 0 = healthy.
// tensorlay-cli gpu
//     One GpuMonitor sample as JSON.

PlatformEnv.Initialize();

string cmd = args.Length > 0 ? args[0] : "help";
string? id = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
string dir = Option("--dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ai-hub");
int healthTimeout = int.TryParse(Option("--health-timeout"), out int t) ? t : 600;

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Log(string tag, string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{tag}] {line}");

switch (cmd)
{
    case "list":
        foreach (var s in ServiceRegistry.GetAll())
            Console.WriteLine($"{s.Id,-10} {s.Port,6}  {s.DisplayName}");
        return 0;

    case "install":
    {
        var def = Find(id);
        using var installer = new InstallerService();
        installer.InstallLog += (sid, line) => Log($"install:{sid}", line);
        var sw = Stopwatch.StartNew();
        try
        {
            await installer.Install(def, dir);
        }
        catch (Exception ex)
        {
            Log("cli", $"INSTALL FAILED after {sw.Elapsed:mm\\:ss}: {ex.Message}");
            return 1;
        }
        bool ok = installer.IsInstalled(def, dir);
        Log("cli", $"install finished in {sw.Elapsed:mm\\:ss}; IsInstalled={ok}");
        return ok ? 0 : 1;
    }

    case "start":
    {
        var def = Find(id);
        using var pm = new ProcessManager();
        using var health = new HealthCheckService();
        var healthy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pm.OutputReceived += (sid, line) => Log(sid, line);
        pm.ProcessExited += (sid, code) => { Log("cli", $"process exited with code {code}"); healthy.TrySetResult(false); };
        health.HealthChanged += (sid, ok) => { if (ok) healthy.TrySetResult(true); };

        var sw = Stopwatch.StartNew();
        pm.StartService(def, dir);
        health.RegisterService(def.Id, $"http://127.0.0.1:{def.Port}", def.HealthEndpoint);

        var winner = await Task.WhenAny(healthy.Task, Task.Delay(TimeSpan.FromSeconds(healthTimeout)));
        bool ok = winner == healthy.Task && healthy.Task.Result;
        Log("cli", ok
            ? $"HEALTHY after {sw.Elapsed:mm\\:ss} (GET :{def.Port}{def.HealthEndpoint})"
            : $"NOT HEALTHY after {sw.Elapsed:mm\\:ss}");
        health.UnregisterService(def.Id);
        await pm.StopService(def.Id);
        return ok ? 0 : 1;
    }

    case "gpu":
    {
        using var mon = new GpuMonitor();
        var tcs = new TaskCompletionSource<TensorLay.Models.GpuInfo>();
        mon.GpuInfoUpdated += info => tcs.TrySetResult(info);
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (winner != tcs.Task) { Console.WriteLine("no GPU sample within 10 s"); return 1; }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(tcs.Task.Result,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    default:
        Console.WriteLine("usage: tensorlay-cli list | install <id> [--dir DIR] | start <id> [--dir DIR] [--health-timeout SEC] | gpu");
        return cmd == "help" ? 0 : 2;
}

static TensorLay.Models.ServiceDefinition Find(string? id) =>
    ServiceRegistry.GetAll().FirstOrDefault(s => s.Id == id)
    ?? throw new ArgumentException($"unknown service id '{id}' (see `list`)");
