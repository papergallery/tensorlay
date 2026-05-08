namespace TensorLay.Models;

public class ServiceDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Port { get; init; }
    public int VramMb { get; init; }
    public string Category { get; init; } = "";
    public string HealthEndpoint { get; init; } = "";
    public string GitRepoUrl { get; init; } = "";
    public string RelativeInstallPath { get; init; } = "";
    public string StartExecutable { get; init; } = "";
    public string StartArguments { get; init; } = "";
    public string ModelsSubfolder { get; init; } = "";
    public bool UseSystemInstaller { get; init; }

    // Python interpreter used at install time for `<exe> <args> -m pip install -r ...`.
    // Defaults to "python" with no extra args. Override when a service pins
    // an older Python (e.g. sd-forge needs 3.10 because torch 2.3.1 has no
    // wheels for 3.13+, so we use the Windows Python Launcher: "py" + "-3.10").
    public string PythonExecutable { get; init; } = "python";
    public string PythonInterpreterArgs { get; init; } = "";

    // Extra pip steps to run AFTER the runtime is ready but BEFORE
    // requirements*.txt. Each entry is the argument string passed to
    // PythonExecutable (e.g. "-3.10 -m pip install torch==2.3.1 torchvision
    // --extra-index-url https://download.pytorch.org/whl/cu121"). Used by
    // sd-forge to pin CUDA torch — requirements_versions.txt lists `torch`
    // unpinned, so plain pip resolves to the CPU wheel and Forge then crashes
    // at start with "Torch not compiled with CUDA enabled".
    public List<string> PreInstallCommands { get; init; } = new();

    // When true, install creates a per-service venv at <targetPath>/.venv
    // using PythonExecutable + PythonInterpreterArgs, and all subsequent
    // pip + start commands route through that venv's python.exe instead of
    // the system py launcher. Isolates dependency conflicts between
    // services — e.g. Forge pins torch 2.3.1 while ComfyUI wants torch
    // 2.7+, which can't coexist on the same system Python install.
    //
    // Backward compat: ProcessManager.StartService and InstallerService
    // detect a missing .venv on already-installed services and fall back
    // to the legacy py-launcher path with a warning, so 0.9.8-era installs
    // keep working — migration happens on next Uninstall+Install.
    public bool UsesVenv { get; init; }

    // Subdirectory (relative to RelativeInstallPath) that the Models tab
    // scans for installed model files. Defaults to ModelsSubfolder when
    // empty, which is correct for services that keep all models in one
    // flat folder. Override to a parent directory for services like
    // ComfyUI/sd-forge that split models across siblings of
    // ModelsSubfolder (LoRA, VAE, upscalers, ControlNet, …) — e.g.
    // ModelsSubfolder="models/checkpoints" (where new downloads land)
    // and ModelsScanRoot="models" (where the Models tab walks).
    public string ModelsScanRoot { get; init; } = "";

    // Override the auto-detected requirements file. By default the
    // installer picks the first of {requirements.txt, requirements_versions.txt}
    // that exists in the repo root. Set this when a service ships multiple
    // (e.g. AllTalk's `requirements_standalone.txt` for the no-text-gen-webui
    // path) and the auto-detected one would pull the wrong dep set.
    public string? RequirementsFileName { get; init; }

    // Extra environment variables set on the child process when the
    // service starts. ProcessManager copies these into
    // ProcessStartInfo.EnvironmentVariables in addition to the inherited
    // user environment. Used by services that don't accept a port via CLI:
    //   TripoSR     GRADIO_SERVER_PORT=7862  (gradio_app.py picks it up)
    //   MusicGen    GRADIO_SERVER_PORT=7861  (audiocraft demo, same pattern)
    //   AllTalk TTS COQUI_TOS_AGREED=1       (skips the XTTS license prompt
    //                                         that would otherwise stall
    //                                         the start-up on first launch)
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();

    // Pip / shell commands run AFTER requirements*.txt has been installed,
    // routed through the per-service venv python when UsesVenv is true.
    // Same string-prefix-of-args layout as PreInstallCommands. Used for:
    //   AllTalk: install DeepSpeed wheel + downloader-prefetched XTTS model
    //   MusicGen: re-pin a known-good audiocraft commit when upstream main
    //             breaks the demos/musicgen_app.py entrypoint
    //   TripoSR: torchmcubes from-source rebuild (PyPI wheel is CPU-only,
    //            blocks the gradio app at first marching-cubes call)
    // Empty list = no-op, fully optional.
    public List<string> PostInstallCommands { get; init; } = new();
}
