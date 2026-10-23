using TensorLay.Models;

namespace TensorLay;

// macOS (Apple Silicon) service catalogue. Same ids, ports, health endpoints
// and model folders as the Windows list — only the install recipe differs:
//
//   * Python comes from uv, not py.exe. PythonInterpreterArgs keeps the
//     "-3.X" selector convention; InstallerService turns it into
//     `uv venv --python 3.X --seed .venv` and strips it from every pip/start
//     command once the venv exists. PythonExecutable/StartExecutable are a
//     plain "python3" (they are replaced by .venv/bin/python at run time).
//   * PyTorch comes from PyPI as-is: its macOS arm64 wheels ARE the GPU build
//     (Metal/MPS). There is no +cuXXX index to pin. Versions mirror the
//     Windows pins where a service is hard-pinned (2.3.1 / 2.1.0) so both
//     platforms run the same library stack; every one of them has a
//     cp310/cp311 macosx_11_0_arm64 wheel on PyPI (checked 2026-10-23).
//   * PYTORCH_ENABLE_MPS_FALLBACK=1 on every torch service: ops MPS lacks
//     run on the CPU instead of raising NotImplementedError mid-generation.
//   * Gradio/HTTP servers bind 127.0.0.1 — the tunnel forwards loopback, and
//     binding 0.0.0.0 would also pop the macOS firewall prompt.
//
// Services that still choose their device as "cuda if available else cpu"
// (TripoSR, MusicGen, AllTalk/XTTS, Faster-Whisper/CTranslate2) run on the CPU
// here: slower, but they install and work. Moving them onto MPS/MLX is
// per-service follow-up work verified on real hardware, not guessed here.
public static partial class ServiceRegistry
{
    private static Dictionary<string, string> MpsEnv(params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string> { { "PYTORCH_ENABLE_MPS_FALLBACK", "1" } };
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    private static List<ServiceDefinition> MacServices() => new()
    {
        new ServiceDefinition
        {
            // Forge on Apple Silicon follows the upstream webui-macos-env.sh
            // recipe: skip the CUDA probe, upcast sampling (MPS has no fp16
            // attention fast path), keep the VAE in fp32 (fp16 VAE on MPS
            // produces black images), interrogate on CPU. TORCH_COMMAND is
            // what launch.py would run if it ever decides torch is missing —
            // pinned to the same wheel the PreInstall step put in the venv.
            Id = "sd-forge",
            DisplayName = "SD Forge",
            Port = 7860,
            VramMb = 8000,
            Category = "image",
            HealthEndpoint = "/sdapi/v1/sd-models",
            GitRepoUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge.git",
            RelativeInstallPath = "sd-forge",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install torch==2.3.1 torchvision==0.18.1"
            },
            EnvironmentVariables = MpsEnv(
                ("TORCH_COMMAND", "pip install torch==2.3.1 torchvision==0.18.1")),
            PostInstallCommands = ForgeLaunchPackages(),
            StartExecutable = "python3",
            StartArguments = "-3.10 launch.py --api --port 7860 --no-download-sd-model " +
                             "--skip-torch-cuda-test --upcast-sampling --no-half-vae --use-cpu interrogate",
            ModelsSubfolder = "models/Stable-diffusion",
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // ComfyUI supports MPS natively and tracks current PyTorch, so —
            // as on Windows — torch is installed unpinned (latest stable).
            // `--listen 127.0.0.1` instead of Windows' bare --listen (0.0.0.0).
            Id = "comfyui",
            DisplayName = "ComfyUI",
            Port = 8188,
            VramMb = 8000,
            Category = "image",
            HealthEndpoint = "/system_stats",
            GitRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git",
            RelativeInstallPath = "comfyui",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.12",
            PreInstallCommands = new List<string>
            {
                "-3.12 -m pip install torch torchvision torchaudio"
            },
            EnvironmentVariables = MpsEnv(),
            StartExecutable = "python3",
            StartArguments = "-3.12 main.py --listen 127.0.0.1 --port 8188",
            ModelsSubfolder = "models/checkpoints",
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // Installed by MacToolchain from ollama-darwin.tgz into the app's
            // tools dir (Metal acceleration is built in). A user-installed
            // Ollama.app is picked up too: PlatformEnv puts its bundled CLI on
            // PATH, and a port already bound is treated as "running".
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
            // TripoSR: torchmcubes is built from source without build
            // isolation against the venv's torch (shared with Windows, see
            // TorchMcubesSource) — CPU marching cubes, clang from the Command
            // Line Tools, cmake/ninja from PyPI. gradio_app.py runs on CPU
            // when CUDA is absent.
            Id = "triposr",
            DisplayName = "TripoSR",
            Port = 7862,
            VramMb = 6000,
            Category = "3d",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/VAST-AI-Research/TripoSR.git",
            RelativeInstallPath = "triposr",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install torch==2.3.1 torchvision==0.18.1",
                Gradio4FastApiPin,
                "-3.10 -m pip install scikit-build-core pybind11 cmake ninja"
            },
            // xatlas==0.0.9 has no macOS arm64 wheel and its sdist bundles a
            // pybind11 that CMake 4 rejects; 0.0.10 ships the wheel, same
            // parametrize() API that TripoSR calls.
            RequirementsReplacements = WithEntries(TripoSrRequirementsReplacements(),
                ("xatlas", "xatlas==0.0.10")),
            PostInstallCommands = new List<string>
            {
                "-m pip install --no-build-isolation " + TorchMcubesSource
            },
            EnvironmentVariables = MpsEnv(
                ("GRADIO_SERVER_PORT", "7862"),
                ("GRADIO_SERVER_NAME", "127.0.0.1")),
            StartExecutable = "python3",
            StartArguments = "-3.10 gradio_app.py",
            ModelsSubfolder = "",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // AllTalk: same requirement swaps and source patches as Windows
            // (shared helpers), plus dropping the two nvidia-*-cu11 lines —
            // CUDA runtime wheels that exist for Windows/Linux only and make
            // pip fail outright on macOS.
            Id = "alltalk",
            DisplayName = "AllTalk TTS",
            Port = 7851,
            VramMb = 6000,
            Category = "audio",
            HealthEndpoint = "/api/ready",
            GitRepoUrl = "https://github.com/erew123/alltalk_tts.git",
            RelativeInstallPath = "alltalk",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.11",
            RequirementsFileName = "system/requirements/requirements_standalone.txt",
            RequirementsReplacements = WithEntries(AllTalkRequirementsReplacements(),
                ("nvidia-cublas-cu11", ""),
                ("nvidia-cudnn-cu11", "")),
            SourceReplacements = AllTalkSourceReplacements(),
            PreInstallCommands = new List<string>
            {
                "-3.11 -m pip install torch==2.3.1 torchaudio==2.3.1",
                "-3.11 -m pip install \"setuptools<81\""
            },
            EnvironmentVariables = MpsEnv(("COQUI_TOS_AGREED", "1")),
            StartExecutable = "python3",
            StartArguments = "-3.11 script.py",
            ModelsSubfolder = "models/xtts",
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // MusicGen (audiocraft): same requirements.txt rewrite as Windows
            // plus two macOS-only edits —
            //   * xformers<0.0.23 has no macOS wheel (source build needs CUDA
            //     tooling) -> removed. MusicGen's default attention backend is
            //     plain torch; only the module-level `from xformers import ops`
            //     in modules/transformer.py needs it, patched at start below.
            //   * spacy==3.7.6 has no macosx arm64 wheel -> 3.7.5, which does.
            Id = "musicgen",
            DisplayName = "MusicGen",
            Port = 7861,
            VramMb = 8000,
            Category = "audio",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/facebookresearch/audiocraft.git",
            RelativeInstallPath = "audiocraft",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install torch==2.1.0 torchaudio==2.1.0 torchvision==0.16.0",
                Gradio4FastApiPin,
                "-3.10 -c \"f='requirements.txt';s=open(f,encoding='utf-8').read();s=s.replace('av==11.0.0','av>=12,<14').replace('transformers>=4.31.0','transformers>=4.31.0,<4.41').replace('gradio','gradio>=4.0,<4.37').replace('xformers<0.0.23','').replace('spacy==3.7.6','spacy==3.7.5');open(f,'w',encoding='utf-8').write(s)\""
            },
            PostInstallCommands = new List<string>
            {
                "-m pip install -e ."
            },
            SourceReplacements = new Dictionary<string, Dictionary<string, string>>
            {
                ["audiocraft/modules/transformer.py"] = new Dictionary<string, string>
                {
                    {
                        "\nfrom xformers import ops\n",
                        "\ntry:\n    from xformers import ops\nexcept ImportError:  # TensorLay (macOS): no xformers; MusicGen uses the torch attention backend\n    ops = None\n"
                    },
                },
            },
            EnvironmentVariables = MpsEnv(
                ("GRADIO_SERVER_PORT", "7861"),
                ("GRADIO_SERVER_NAME", "127.0.0.1")),
            StartExecutable = "python3",
            StartArguments = "-3.10 -m demos.musicgen_app",
            ModelsSubfolder = "",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // Same tensorlay-whisper server as Windows. CTranslate2 4.7.2
            // ships a cp312 macosx arm64 wheel; there is no CUDA, so the
            // server's own fallback lands on cpu/int8.
            Id = "whisper",
            DisplayName = "Faster-Whisper",
            Port = 9000,
            VramMb = 3000,
            Category = "audio",
            HealthEndpoint = "/health",
            GitRepoUrl = "https://github.com/papergallery/tensorlay-whisper.git",
            RelativeInstallPath = "whisper",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.12",
            EnvironmentVariables = new Dictionary<string, string>
            {
                // Skip the doomed CUDA attempt at startup.
                { "WHISPER_DEVICE", "cpu" },
            },
            StartExecutable = "python3",
            StartArguments = "-3.12 -m uvicorn server:app --host 127.0.0.1 --port 9000",
            ModelsSubfolder = "",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // F5-TTS 1.1.9 with the Windows pin set minus +cu121. Its own
            // metadata already skips bitsandbytes on Darwin/arm64, and F5
            // picks MPS by itself when available. The Trainer-import stub is
            // kept (inference never uses Trainer; skips the datasets/pyarrow
            // import chain) — the site-packages path differs from Windows.
            Id = "f5-tts",
            DisplayName = "F5-TTS",
            Port = 7863,
            VramMb = 4000,
            Category = "audio",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/SWivid/F5-TTS.git",
            RelativeInstallPath = "f5-tts",
            PythonExecutable = "python3",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install torch==2.3.1 torchaudio==2.3.1",
                "-3.10 -m pip install \"numpy<2\" \"transformers>=4.40,<5\" \"gradio>=5.0,<6\" \"pyarrow==15.0.2\" \"datasets<4.1\""
            },
            PostInstallCommands = new List<string>
            {
                "-m pip install \"f5-tts==1.1.9\""
            },
            EnvironmentVariables = MpsEnv(
                ("GRADIO_SERVER_PORT", "7863"),
                ("GRADIO_SERVER_NAME", "127.0.0.1"),
                ("PYTHONFAULTHANDLER", "1")),
            SourceReplacements = new Dictionary<string, Dictionary<string, string>>
            {
                [".venv/lib/python3.10/site-packages/f5_tts/model/__init__.py"] = new Dictionary<string, string>
                {
                    { "from f5_tts.model.trainer import Trainer", "Trainer = None  # stubbed by TensorLay: avoids datasets->pyarrow import chain; inference doesn't use Trainer" },
                },
            },
            StartExecutable = "python3",
            StartArguments = "-3.10 -m f5_tts.infer.infer_gradio --port 7863 --host 127.0.0.1",
            ModelsSubfolder = "",
            UseSystemInstaller = false,
            UsesVenv = true
        }
    };

    private static Dictionary<string, string> WithEntries(
        Dictionary<string, string> map, params (string Key, string Value)[] extra)
    {
        foreach (var (k, v) in extra) map[k] = v;
        return map;
    }
}
