using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using TensorLay.Models;

namespace TensorLay.Services;

public class InstallerService : IDisposable
{
    private const string OllamaInstallerPrimaryUrl = "https://ollama.com/download/OllamaSetup.exe";
    // GitHub Releases is the upstream source ollama.com mirrors from. Used
    // as a fallback when ollama.com's CDN is degraded — verified live in
    // 0.9.2 with a 200 Mbit user where ollama.com stalled after handshake
    // and we waited the full 30-min HttpClient timeout with zero bytes.
    private const string OllamaInstallerFallbackUrl = "https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe";

    // Idle (between-chunks) read timeout. The OllamaSetup.exe download is
    // ~800 MB; on a healthy connection any 60-sec gap means the CDN has
    // wedged and we should fail fast instead of blocking forever.
    private static readonly TimeSpan OllamaIdleReadTimeout = TimeSpan.FromSeconds(60);
    private const int OllamaMaxAttemptsPerUrl = 3;
    // Anything smaller than this is dropped on retry rather than resumed —
    // a tiny partial is more likely a redirect/error body than real data,
    // and `Range: bytes=N-` against a server that doesn't send Content-Range
    // would silently give us a bogus file otherwise.
    private const long OllamaResumeMinBytes = 100L * 1024 * 1024;

    private bool _disposed;

    public event Action<string, string>? InstallLog;
    public event Action<string, double>? InstallProgress;

    public async Task Install(ServiceDefinition service, string installDir)
    {
        try
        {
            if (service.UseSystemInstaller)
            {
                await InstallOllama(service.Id);
                return;
            }

            if (string.IsNullOrEmpty(service.GitRepoUrl))
            {
                // Service has nothing to clone (e.g. system-installer services
                // are handled above). Treat as a no-op — the registry is the
                // source of truth for whether a service supports `Install`.
                return;
            }

            string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
                ? installDir
                : Path.Combine(installDir, service.RelativeInstallPath);

            EnsureOnPath("git", "Git", "https://git-scm.com/download/win");

            Directory.CreateDirectory(installDir);

            InstallLog?.Invoke(service.Id, $"Cloning {service.GitRepoUrl}...");
            InstallProgress?.Invoke(service.Id, 0.1);

            await RunProcess("git", $"clone {service.GitRepoUrl} \"{targetPath}\"", installDir, service.Id);

            // If TensorLay was launched elevated (e.g. inherited admin token
            // from an installer auto-launch — fixed in installer.nsi as of
            // 0.8.8 but still possible on legacy installs), the cloned files
            // are owned by BUILTIN/Administrators. When Forge later runs git
            // under the user account it refuses to operate on the repo with
            // "fatal: detected dubious ownership". Mark the cloned tree (and
            // its known sub-repos directory) as safe so git doesn't trip on
            // ownership mismatch. Path uses forward slashes — git's
            // safe.directory entries are case-sensitive and match the path
            // git itself reports.
            string safePath = targetPath.Replace('\\', '/');
            await RunProcess("git", $"config --global --add safe.directory \"{safePath}\"", installDir, service.Id);
            await RunProcess("git", $"config --global --add safe.directory \"{safePath}/repositories/*\"", installDir, service.Id);

            InstallProgress?.Invoke(service.Id, 0.6);

            // Pick the requirements file: explicit override on the
            // ServiceDefinition wins (AllTalk ships several variants and
            // we want the standalone one), otherwise auto-detect among
            // the common names. SD Forge ships only requirements_versions.txt;
            // other forks vary.
            string? requirementsFile = null;
            if (!string.IsNullOrEmpty(service.RequirementsFileName)
                && File.Exists(Path.Combine(targetPath, service.RequirementsFileName)))
            {
                requirementsFile = service.RequirementsFileName;
            }
            else
            {
                foreach (var candidate in new[] { "requirements.txt", "requirements_versions.txt" })
                {
                    if (File.Exists(Path.Combine(targetPath, candidate)))
                    {
                        requirementsFile = candidate;
                        break;
                    }
                }
            }
            // Run the Python install pipeline (venv + Pre/PostInstall) whenever
            // the service does ANY Python work — not only when a requirements.txt
            // exists. pyproject-only services (speaches/whisper, F5-TTS) ship no
            // requirements.txt and do their real install in PostInstallCommands
            // ("pip install -e ."); gating the whole block on requirementsFile
            // meant those services git-cloned and then silently did nothing —
            // no venv, no editable install — yet still reported success and then
            // failed at start. Only the `pip install -r` step itself is gated on
            // requirementsFile below.
            bool needsPythonSetup = requirementsFile is not null
                || service.UsesVenv
                || service.PreInstallCommands.Count > 0
                || service.PostInstallCommands.Count > 0;
            if (needsPythonSetup)
            {
                // For sd-forge etc. service.PythonExecutable is "py" with
                // PythonInterpreterArgs="-3.10" — must match the runtime
                // launcher so pip installs into the same Python that runs
                // the service.
                EnsureOnPath(service.PythonExecutable, "Python 3", "https://www.python.org/downloads/windows/");

                // The new Windows Python Launcher (bundled with Python 3.13+)
                // can fetch missing runtimes on demand via `py install 3.10`.
                // Idempotent on the new launcher; will fail loudly on older
                // launchers — we swallow that and let the actual pip step
                // produce the real error if the runtime is still missing.
                if (service.PythonExecutable == "py" && service.PythonInterpreterArgs.StartsWith("-3."))
                {
                    string version = service.PythonInterpreterArgs.Split(' ')[0].TrimStart('-');
                    InstallLog?.Invoke(service.Id, $"Ensuring Python {version} runtime is installed (one-time, ~30 MB)...");
                    try
                    {
                        await RunProcess("py", $"install {version}", targetPath, service.Id);
                    }
                    catch (Exception ex)
                    {
                        InstallLog?.Invoke(service.Id, $"py install warning (continuing): {ex.Message}");
                    }
                }

                // Per-service venv (when enabled in the registry). Build it
                // *with* the version-pinned launcher invocation, then route
                // every subsequent pip call through the venv's python.exe.
                // Once routed, the leading "-3.X " selector is meaningless
                // (and harmful — venv python doesn't accept it), so the
                // helper below strips it from the command string.
                string pyExe = service.PythonExecutable;
                string pyArgsPrefix = service.PythonInterpreterArgs;
                if (service.UsesVenv)
                {
                    string venvPath = Path.Combine(targetPath, ".venv");
                    string venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
                    if (!File.Exists(venvPython))
                    {
                        InstallLog?.Invoke(service.Id, $"Creating isolated venv at {venvPath}...");
                        string venvArgs = string.IsNullOrEmpty(pyArgsPrefix)
                            ? $"-m venv \"{venvPath}\""
                            : $"{pyArgsPrefix} -m venv \"{venvPath}\"";
                        await RunProcess(pyExe, venvArgs, targetPath, service.Id);
                    }
                    else
                    {
                        InstallLog?.Invoke(service.Id, "Reusing existing venv.");
                    }
                    pyExe = venvPython;
                    pyArgsPrefix = ""; // venv python ignores -3.X selectors
                }

                // PreInstallCommands run before requirements*.txt so a service
                // can pin specific wheels (e.g. CUDA torch from a custom index)
                // that requirements_versions.txt would otherwise satisfy with
                // the wrong build.
                //
                // RTX 50 / Blackwell guard: SD Forge's PreInstallCommand pins
                // torch==2.3.1+cu121, which has no Blackwell support (cu121
                // tops out at sm_90). The install will succeed but the
                // service runs CPU-only — the symptom is "works but unusably
                // slow." Surface that explicitly so the user isn't left
                // diagnosing it from a perf cliff. We don't auto-bump torch
                // because Forge upstream is pinned to 2.3.1 and a blind
                // version swap risks breaking image generation in subtler
                // ways (model loaders, ControlNet patches, etc.).
                bool hasCu121Pin = service.PreInstallCommands.Any(c => c.Contains("cu121"));
                if (hasCu121Pin)
                {
                    var gen = await GpuMonitor.DetectGenerationAsync().ConfigureAwait(false);
                    if (gen == GpuGeneration.Rtx50)
                    {
                        InstallLog?.Invoke(service.Id,
                            "WARNING: RTX 50-series (Blackwell) detected. This service pins torch 2.3.1+cu121, " +
                            "which lacks Blackwell support — it will install but likely fall back to CPU. " +
                            "Consider ComfyUI (cu128) instead until Forge upstream bumps torch.");
                    }
                }

                foreach (var rawCmd in service.PreInstallCommands)
                {
                    string preCmd = service.UsesVenv ? StripVersionSelector(rawCmd) : rawCmd;
                    InstallLog?.Invoke(service.Id, $"Pre-install: {pyExe} {preCmd}");
                    await RunProcess(pyExe, preCmd, targetPath, service.Id);
                }

                if (requirementsFile is not null)
                {
                    InstallLog?.Invoke(service.Id, $"Installing pip requirements ({requirementsFile})...");
                    string pipArgs = string.IsNullOrEmpty(pyArgsPrefix)
                        ? $"-m pip install -r {requirementsFile}"
                        : $"{pyArgsPrefix} -m pip install -r {requirementsFile}";
                    await RunProcess(pyExe, pipArgs, targetPath, service.Id);
                }

                // PostInstallCommands run AFTER the requirements step. Used
                // for services that need an extra wheel / model prefetch /
                // from-source rebuild that a flat requirements.txt can't
                // express (e.g. AllTalk's DeepSpeed wheel, TripoSR's
                // torchmcubes CUDA recompile).
                foreach (var rawCmd in service.PostInstallCommands)
                {
                    string postCmd = service.UsesVenv ? StripVersionSelector(rawCmd) : rawCmd;
                    InstallLog?.Invoke(service.Id, $"Post-install: {pyExe} {postCmd}");
                    await RunProcess(pyExe, postCmd, targetPath, service.Id);
                }
            }

            // Successful-install marker. Without this, IsInstalled reports
            // true on the mere existence of the install dir, including
            // half-cloned repos, half-installed pip envs, and Stop-mid-pip
            // states. The marker is written ONLY after every step above
            // ran without throwing — its presence is the ground-truth
            // "this install is usable" signal.
            try
            {
                File.WriteAllText(
                    Path.Combine(targetPath, ".tensorlay-installed"),
                    $"version=1\nservice={service.Id}\ninstalled_at={DateTime.UtcNow:o}\n");
            }
            catch (Exception ex)
            {
                // Marker write failure is not fatal — the install itself
                // succeeded. Log so a future "why is this still showing
                // NotInstalled" debug session has the answer.
                InstallLog?.Invoke(service.Id, $"warning: could not write install marker: {ex.Message}");
            }

            InstallProgress?.Invoke(service.Id, 1.0);
            InstallLog?.Invoke(service.Id, "Installation complete.");
        }
        catch (Exception ex)
        {
            InstallLog?.Invoke(service.Id, $"ERROR: {ex.Message}");
            throw;
        }
    }

    private async Task InstallOllama(string serviceId)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

        InstallLog?.Invoke(serviceId, "Downloading OllamaSetup.exe...");
        InstallProgress?.Invoke(serviceId, 0.0);

        // Try ollama.com first, then GitHub Releases. Within each URL,
        // retry up to N times with a short backoff. Resume across attempts
        // is handled inside DownloadOllamaInstaller so a stall at 700 MB
        // doesn't restart the whole download.
        Exception? lastError = null;
        bool downloaded = false;
        foreach (var url in new[] { OllamaInstallerPrimaryUrl, OllamaInstallerFallbackUrl })
        {
            string host = new Uri(url).Host;
            for (int attempt = 1; attempt <= OllamaMaxAttemptsPerUrl; attempt++)
            {
                try
                {
                    await DownloadOllamaInstaller(serviceId, url, tempPath);
                    downloaded = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    InstallLog?.Invoke(serviceId,
                        $"Download attempt {attempt}/{OllamaMaxAttemptsPerUrl} from {host} failed: {ex.Message}");
                    if (attempt < OllamaMaxAttemptsPerUrl)
                        await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            if (downloaded) break;
            if (url == OllamaInstallerPrimaryUrl)
                InstallLog?.Invoke(serviceId, $"Falling back to GitHub Releases ({OllamaInstallerFallbackUrl})");
        }
        if (!downloaded)
        {
            // Surface a useful error and the manual-install URL — gives the
            // user a path forward instead of a stuck progress bar.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw new InvalidOperationException(
                $"Could not download OllamaSetup.exe from ollama.com or GitHub after {OllamaMaxAttemptsPerUrl} attempts each. " +
                $"Last error: {lastError?.Message ?? "(unknown)"}. " +
                "Install manually from https://ollama.com/download then click Start.");
        }

        InstallProgress?.Invoke(serviceId, 0.5);
        InstallLog?.Invoke(serviceId, "Running Ollama installer...");

        var psi = new ProcessStartInfo(tempPath) { UseShellExecute = true };
        var process = Process.Start(psi);
        if (process is not null)
            await process.WaitForExitAsync();

        InstallProgress?.Invoke(serviceId, 1.0);
        InstallLog?.Invoke(serviceId, "Ollama installation complete.");
    }

    private async Task DownloadOllamaInstaller(string serviceId, string url, string tempPath)
    {
        // If a substantial partial file is on disk from a prior attempt,
        // resume rather than restart. Anything below the threshold is
        // discarded — too risky to glue onto without a strong size signal.
        long resumeFrom = 0;
        if (File.Exists(tempPath))
        {
            long existing = new FileInfo(tempPath).Length;
            if (existing >= OllamaResumeMinBytes)
            {
                resumeFrom = existing;
                InstallLog?.Invoke(serviceId, $"Resuming from {existing / 1_048_576} MB");
            }
            else if (existing > 0)
            {
                File.Delete(tempPath);
            }
        }

        // Timeout.InfiniteTimeSpan disables HttpClient's total-request
        // timeout — we enforce idleness per-chunk via CancellationToken
        // below, which is the right abstraction for a multi-hundred-MB
        // download (a slow but progressing connection should not be killed).
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        // If we asked for a Range and the server returned 200 instead of
        // 206, it ignored our request — start over from byte 0 (otherwise
        // we'd append the full file onto the end of the partial).
        if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            InstallLog?.Invoke(serviceId, "Server ignored Range header; restarting from 0");
            File.Delete(tempPath);
            resumeFrom = 0;
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        // ContentLength on a 206 response is "remaining bytes", not total —
        // add resumeFrom to derive the total for progress calculation.
        long? remaining = response.Content.Headers.ContentLength;
        long totalSize = remaining is > 0 ? remaining.Value + resumeFrom : 0;
        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(
            tempPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write);

        var buffer = new byte[81920];
        long copied = resumeFrom;
        int reportTick = 0;
        while (true)
        {
            // Per-read idle timeout: the CDN may keep the TCP socket alive
            // while sending zero bytes. HttpClient.Timeout=InfiniteTimeSpan
            // means our only escape from that scenario is this CTS.
            using var idleCts = new CancellationTokenSource(OllamaIdleReadTimeout);
            int read;
            try
            {
                read = await src.ReadAsync(buffer.AsMemory(), idleCts.Token);
            }
            catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
            {
                throw new IOException(
                    $"Download stalled — no data received in {OllamaIdleReadTimeout.TotalSeconds:0}s " +
                    $"(got {copied / 1_048_576} MB of " +
                    $"{(totalSize > 0 ? (totalSize / 1_048_576).ToString() + " MB" : "unknown size")}).");
            }
            if (read == 0) break;

            await dst.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
            // 0.0..0.45 = download phase, 0.5+ = installer running phase.
            // Throttle progress to ~every 64 reads (~5 MB) to avoid UI churn.
            if (totalSize > 0 && (++reportTick & 0x3F) == 0)
            {
                InstallProgress?.Invoke(serviceId, 0.45 * ((double)copied / totalSize));
            }
        }
    }

    // Back-compat overload for the install-failure cleanup path in
    // ServiceViewModel.Install — that flow always wants a full wipe.
    public Task Uninstall(ServiceDefinition service, string installDir)
        => Uninstall(service, installDir, keepModels: false);

    public async Task Uninstall(ServiceDefinition service, string installDir, bool keepModels)
    {
        if (service.UseSystemInstaller)
        {
            // Ollama (the only system-installer service today) is installed via
            // its own MSI/setup into Program Files — we have no clean way to
            // call its uninstaller, and silently doing nothing while the UI
            // says "Uninstalled" leaves the user confused on next launch.
            throw new NotSupportedException(
                $"{service.DisplayName} was installed by its own setup. " +
                "Remove it via Windows Settings → Apps & Features.");
        }

        string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        if (!Directory.Exists(targetPath)) return;

        // Models live under ModelsScanRoot when the service uses split
        // category folders (ComfyUI: models/checkpoints, models/loras, …),
        // and ModelsSubfolder for flat layouts. Falling back to the second
        // covers older registry entries where ScanRoot wasn't set.
        string modelsRel = string.IsNullOrEmpty(service.ModelsScanRoot)
            ? service.ModelsSubfolder
            : service.ModelsScanRoot;

        if (keepModels && !string.IsNullOrEmpty(modelsRel))
        {
            await Task.Run(() => UninstallExceptModels(service, targetPath, modelsRel));
        }
        else
        {
            InstallLog?.Invoke(service.Id, $"Removing {targetPath}...");
            await Task.Run(() => SoftDeleteDirectory(service.Id, targetPath));
            InstallLog?.Invoke(service.Id, "Uninstalled.");
        }
    }

    // Wipe everything under targetPath except the configured models folder.
    // Used by the "Keep models on uninstall" path so re-installing doesn't
    // force the user to redownload tens of GB. Walks top-level entries —
    // anything matching the relative models prefix (e.g. "models" or
    // "models/checkpoints") is preserved, the rest is sent to the Recycle
    // Bin via SoftDeleteDirectory / SoftDeleteFile.
    private void UninstallExceptModels(ServiceDefinition service, string targetPath, string modelsRel)
    {
        // Normalize to platform separators and to the *first* segment —
        // we preserve whichever top-level directory contains the models
        // tree (typical layouts are "models" or "models/checkpoints"; the
        // first segment is "models" in both cases).
        string firstSegment = modelsRel.Replace('/', Path.DirectorySeparatorChar)
                                       .Split(Path.DirectorySeparatorChar)[0];
        string preserveAbs = Path.Combine(targetPath, firstSegment);

        InstallLog?.Invoke(service.Id, $"Removing {targetPath} (preserving {preserveAbs})...");
        foreach (var entry in Directory.EnumerateFileSystemEntries(targetPath))
        {
            if (string.Equals(entry, preserveAbs, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (Directory.Exists(entry))
                    SoftDeleteDirectory(service.Id, entry);
                else
                    SoftDeleteFile(service.Id, entry);
            }
            catch (Exception ex)
            {
                InstallLog?.Invoke(service.Id, $"WARN: could not remove {entry}: {ex.Message}");
            }
        }
        InstallLog?.Invoke(service.Id, "Uninstalled (models folder preserved).");
    }

    // Send a directory to the Recycle Bin so a misclick is recoverable
    // through Windows' built-in restore. Recycle Bin has a per-drive size
    // limit (~5–10% of disk by default); when Windows determines the
    // payload won't fit, it falls back to permanent deletion silently —
    // we log that case so a confused user has a thread to pull on.
    //
    // git on Windows marks pack files read-only, which made the legacy
    // Directory.Delete path throw halfway through; we still clear the
    // attribute on the way in so the VB DeleteDirectory call doesn't
    // hit the same issue (UIOption.OnlyErrorDialogs would surface the
    // dialog and freeze the UI thread).
    private void SoftDeleteDirectory(string serviceId, string path)
    {
        if (!Directory.Exists(path)) return;
        ClearReadOnlyRecursive(path);
        try
        {
            // ShowUI=OnlyErrorDialogs avoids the standard "Are you sure?" prompt
            // (we already showed our own confirm) but still surfaces *errors*,
            // which is what we want — a permission/lock failure shouldn't be
            // silent. UICancelOption.DoNothing means a user-cancel doesn't throw.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }
        catch (Exception ex)
        {
            InstallLog?.Invoke(serviceId,
                $"WARN: Recycle Bin delete failed ({ex.Message}); falling back to permanent delete.");
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex2)
            {
                InstallLog?.Invoke(serviceId, $"ERROR: permanent delete also failed: {ex2.Message}");
                throw;
            }
        }
    }

    private void SoftDeleteFile(string serviceId, string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* best-effort */ }
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }
        catch (Exception ex)
        {
            InstallLog?.Invoke(serviceId,
                $"WARN: Recycle Bin delete failed for {path} ({ex.Message}); falling back to permanent delete.");
            try { File.Delete(path); } catch { /* swallow */ }
        }
    }

    private static void ClearReadOnlyRecursive(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            catch { /* best-effort */ }
        }
    }

    public bool IsInstalled(ServiceDefinition service, string installDir)
    {
        if (service.UseSystemInstaller)
        {
            // Only Ollama uses UseSystemInstaller today; check whether ollama is on PATH.
            return IsOnPath("ollama");
        }

        string targetPath = string.IsNullOrEmpty(service.RelativeInstallPath)
            ? installDir
            : Path.Combine(installDir, service.RelativeInstallPath);

        if (!Directory.Exists(targetPath)) return false;

        // Prefer the .tensorlay-installed marker — it's only written when
        // every install step (clone, pip, pre/post commands) ran clean.
        // Half-cloned dirs, mid-pip-failure leftovers, and ctrl-C-aborted
        // installs all leave the directory but no marker, and we report
        // them as NotInstalled so the user can re-Install without manually
        // rmdir'ing.
        if (File.Exists(Path.Combine(targetPath, ".tensorlay-installed")))
            return true;

        // Legacy back-compat: pre-v0.9.11 installs predate the marker.
        // For services already on disk before this release, treat the
        // directory itself as the signal — same as the old behavior. Once
        // the user re-installs (or runs a future migration), the marker
        // will be written and we'll be back on the strict path.
        bool legacyHasContent = Directory.EnumerateFileSystemEntries(targetPath).Any();
        return legacyHasContent;
    }

    // Strip a leading py-launcher version selector ("-3.10 ", "-3.12 ") from
    // a command-args string. Used when re-routing a service's pip command
    // through a venv python — the selector is invalid for a regular python
    // invocation and python.exe would treat "-3.10" as an unknown option.
    internal static string StripVersionSelector(string args)
    {
        if (string.IsNullOrEmpty(args)) return args;
        return System.Text.RegularExpressions.Regex.Replace(args, @"^-3\.\d+\s+", "");
    }

    private static void EnsureOnPath(string exe, string friendlyName, string installUrl)
    {
        if (!IsOnPath(exe))
        {
            throw new InvalidOperationException(
                $"{friendlyName} was not found in PATH. Install it from {installUrl}, then restart TensorLay.");
        }
    }

    private static bool IsOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe)))
                    return true;
            }
            catch { /* skip bad path */ }
        }
        return false;
    }

    private async Task RunProcess(string exe, string args, string workingDir, string serviceId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) InstallLog?.Invoke(serviceId, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) InstallLog?.Invoke(serviceId, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited with code {process.ExitCode}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
