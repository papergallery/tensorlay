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
            StartExecutable = "py",
            StartArguments = "-3.10 launch.py --api --listen --port 7860 --skip-torch-cuda-test --no-download-sd-model",
            ModelsSubfolder = "models/Stable-diffusion",
            UseSystemInstaller = false
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
            StartExecutable = "python",
            StartArguments = "main.py --listen --port 8188",
            ModelsSubfolder = "models/checkpoints",
            UseSystemInstaller = false
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
        },
        new ServiceDefinition
        {
            Id = "alltalk",
            DisplayName = "AllTalk TTS",
            Port = 7851,
            VramMb = 4000,
            Category = "audio",
            HealthEndpoint = "/api/ready",
            GitRepoUrl = "https://github.com/erew123/alltalk_tts.git",
            RelativeInstallPath = "alltalk_tts",
            StartExecutable = "python",
            StartArguments = "start_alltalk.py",
            ModelsSubfolder = "models",
            UseSystemInstaller = false
        },
        new ServiceDefinition
        {
            Id = "musicgen",
            DisplayName = "MusicGen",
            Port = 7861,
            VramMb = 6000,
            Category = "audio",
            HealthEndpoint = "/health",
            GitRepoUrl = "",
            RelativeInstallPath = "musicgen",
            StartExecutable = "python",
            StartArguments = "server.py --port 7861",
            ModelsSubfolder = "models",
            UseSystemInstaller = false
        },
        new ServiceDefinition
        {
            Id = "triposr",
            DisplayName = "TripoSR",
            Port = 7862,
            VramMb = 8000,
            Category = "3d",
            HealthEndpoint = "/health",
            GitRepoUrl = "https://github.com/VAST-AI-Research/TripoSR.git",
            RelativeInstallPath = "triposr",
            StartExecutable = "python",
            StartArguments = "run.py --port 7862",
            ModelsSubfolder = "models",
            UseSystemInstaller = false
        }
    };
}
