using TensorLay.Models;

namespace TensorLay;

public static class ServiceRegistry
{
    public static List<ServiceDefinition> GetAll() => new()
    {
        new ServiceDefinition
        {
            Id = "sd-forge",
            DisplayName = "SD Forge",
            Port = 7860,
            VramMb = 8000,
            Category = "image",
            HealthEndpoint = "/sdapi/v1/sd-models",
            GitRepoUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge.git",
            RelativeInstallPath = "sd-forge",
            // Forge pins torch==2.3.1, which has no wheels for Python 3.13+.
            // Use the Windows Python Launcher (`py.exe`) to force 3.10 even
            // when a newer Python is the system default. The same prefix is
            // used both for `pip install -r requirements_versions.txt` at
            // install time and for launching `launch.py` at start time.
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.10",
            // Forge's requirements_versions.txt lists `torch` unpinned, so a
            // plain pip resolve picks the CPU wheel from PyPI. Install the
            // CUDA wheel first; the requirements step then sees torch already
            // satisfied and skips it.
            //
            // The `+cu121` local version specifier is load-bearing: with plain
            // `torch==2.3.1 --extra-index-url …/cu121` pip is free to pick the
            // CPU wheel from PyPI (extra-index ADDS to indexes, doesn't
            // replace), and on a machine that already has CPU torch installed
            // it would just stay put. `+cu121` makes the CUDA wheel the only
            // version that matches the constraint.
            //
            // `--force-reinstall` covers the case where a prior failed install
            // (e.g. v0.8.5 without PreInstallCommand) left torch-cpu in place.
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install --force-reinstall torch==2.3.1+cu121 torchvision==0.18.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121"
            },
            StartExecutable = "py",
            StartArguments = "-3.10 launch.py --api --listen --port 7860 --no-download-sd-model",
            ModelsSubfolder = "models/Stable-diffusion",
            // Forge keeps SD checkpoints in models/Stable-diffusion but
            // also models/Lora, models/VAE, models/ESRGAN — scan the whole
            // models/ tree so the Models tab reflects everything on disk.
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            Id = "comfyui",
            DisplayName = "ComfyUI",
            Port = 8188,
            VramMb = 8000,
            Category = "image",
            HealthEndpoint = "/system_stats",
            GitRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git",
            RelativeInstallPath = "comfyui",
            // ComfyUI's requirements.txt lists `torch` unpinned. With the
            // system default Python (3.14 on a fresh Windows install today)
            // pip resolves the CPU wheel from PyPI — and even if it didn't,
            // CUDA wheels for cp314 don't exist in the stable PyTorch index
            // yet. Pin to 3.12 via py.exe -3.12 so we get a Python with
            // published CUDA wheels, and pre-install torch from the cu128
            // channel before requirements.txt runs.
            //
            // cu128 is the broad-compatibility choice: works on RTX 30/40/50
            // with NVIDIA drivers >= 555. requirements.txt's plain `torch`
            // line is then satisfied by the +cu128 wheel and skipped.
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.12",
            PreInstallCommands = new List<string>
            {
                "-3.12 -m pip install --force-reinstall torch torchvision torchaudio --extra-index-url https://download.pytorch.org/whl/cu128"
            },
            StartExecutable = "py",
            StartArguments = "-3.12 main.py --listen --port 8188",
            ModelsSubfolder = "models/checkpoints",
            // ComfyUI splits models across many siblings of checkpoints/:
            // loras/, vae/, upscale_models/, clip/, controlnet/,
            // embeddings/, etc. Scan the whole models/ tree so .pth
            // upscalers and .safetensors LoRAs show up in the Models tab.
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            Id = "ollama",
            DisplayName = "Ollama",
            Port = 11434,
            VramMb = 10000,
            Category = "text",
            HealthEndpoint = "/api/tags",
            GitRepoUrl = "",
            RelativeInstallPath = "",
            StartExecutable = "ollama",
            StartArguments = "serve",
            ModelsSubfolder = "",
            UseSystemInstaller = true
        }
        // alltalk, musicgen, triposr removed in v0.9.2 — their installers
        // were broken in distinct ways:
        //   - alltalk doesn't ship a flat requirements.txt; needs atsetup.bat
        //     wrapper invocation (interactive) and start_alltalk.py doesn't
        //     exist (it's a .bat generated by atsetup).
        //   - musicgen had GitRepoUrl="", so Install() always threw
        //     NotImplementedException. Real impl needs an audiocraft HTTP
        //     wrapper (audiocraft has no built-in server).
        //   - triposr's run.py is a CLI batch script for offline inference,
        //     not a server. The HTTP entrypoint is gradio_app.py, which only
        //     reads the port via GRADIO_SERVER_PORT env var (we don't pass
        //     env vars to ProcessManager today). Plus torchmcubes needs a
        //     manual reinstall from source for CUDA support.
        // Bring them back once we have venv-per-service, env-var support in
        // ProcessManager, and proper installer hooks (post-install commands).
    };
}
