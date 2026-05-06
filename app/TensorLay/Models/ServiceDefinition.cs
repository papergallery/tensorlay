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
}
