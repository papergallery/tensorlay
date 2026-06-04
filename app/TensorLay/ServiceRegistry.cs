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
        },
        // ── Returned in v0.9.11 after the prerequisites finally landed:
        //   - venv-per-service (0.9.9)         → no torch-version conflicts
        //   - EnvironmentVariables (0.9.11)     → GRADIO_SERVER_PORT routing
        //   - PostInstallCommands (0.9.11)      → torchmcubes-cuda rebuild
        //   - .tensorlay-installed marker (0.9.11) → broken installs no longer
        //                                            masquerade as "Stopped"
        // Each entry below carries a doc block describing the upstream
        // brittleness that bit us in v0.9.0–v0.9.2 and how the current
        // shape mitigates it. KEEP these comments on edit — they exist so
        // a future maintainer doesn't repeat the v0.9.2 mistake of just
        // deleting the entry when an install fails.
        new ServiceDefinition
        {
            // TripoSR — single-image to 3D mesh.
            // Repo is dormant (last commit 2024) and stable, which is why
            // we trust `main` here without a pinned commit. Their
            // gradio_app.py is the HTTP entrypoint; run.py is a CLI batch
            // script (the trap that bit us in 0.9.2 — we wired StartArguments
            // to run.py and got a one-shot inference, not a server).
            //
            // Port routing: gradio.launch() in gradio_app.py takes no
            // explicit port arg; gradio honors GRADIO_SERVER_PORT env when
            // server_port is unset. Hence the EnvironmentVariables entry
            // below — without it the demo binds 7860 (Forge port collision).
            //
            // CUDA requirement: torchmcubes (marching cubes for the mesh
            // export step) ships a CPU-only wheel on PyPI. The from-source
            // build picks up CUDA when CUDA_HOME is set. PostInstallCommands
            // forces a reinstall against the git source so CUDA is used —
            // without this, mesh export silently runs on CPU and a typical
            // generation takes minutes instead of seconds. If the rebuild
            // fails (no CUDA toolkit installed), we keep the CPU wheel and
            // log a warning — service still works, just slowly.
            Id = "triposr",
            DisplayName = "TripoSR",
            Port = 7862,
            VramMb = 6000,
            Category = "3d",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/VAST-AI-Research/TripoSR.git",
            RelativeInstallPath = "triposr",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                // cu121 matches the upstream torchmcubes build matrix and
                // the same wheel set that Forge uses. RTX 30/40 ok; RTX 50
                // gets the cu121 warning from GpuMonitor (Forge path).
                "-3.10 -m pip install --force-reinstall torch==2.3.1+cu121 torchvision==0.18.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121"
            },
            PostInstallCommands = new List<string>
            {
                // Rebuild torchmcubes from source so CUDA acceleration
                // actually fires. If the user has no CUDA toolkit, this
                // command will fail loudly — the install is still usable
                // (PyPI's CPU wheel was installed by requirements.txt) so
                // we surface the failure but don't roll back. See README
                // notes — TODO add an InstallerService.PostInstallOptional
                // path for steps where failure should be a warning, not a
                // hard error.
                "-m pip install --force-reinstall git+https://github.com/tatsy/torchmcubes.git"
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "GRADIO_SERVER_PORT", "7862" },
                // Bind 127.0.0.1 explicitly — the SSH tunnel forwards
                // 127.0.0.1:7862 to the VPS, and gradio defaults to
                // 0.0.0.0 which is unnecessarily exposed on the LAN.
                { "GRADIO_SERVER_NAME", "127.0.0.1" },
            },
            StartExecutable = "py",
            StartArguments = "-3.10 gradio_app.py",
            ModelsSubfolder = "",   // TripoSR doesn't keep persistent models on disk
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // AllTalk TTS — Coqui XTTS frontend with voice cloning.
            // Repo: erew123/alltalk_tts. Active project; the 0.9.2 removal
            // was driven by atsetup.bat (an interactive batch wrapper) —
            // not by a fundamental brokenness. Their `script.py` is a
            // perfectly fine non-interactive entry point as long as the
            // dependencies are installed. atsetup.bat does (a) make a conda
            // env, (b) install requirements_standalone.txt, (c) run
            // script.py. We replicate (b)+(c) directly via the venv path.
            //
            // RequirementsFileName: AllTalk's repo root has BOTH a
            // requirements.txt (text-generation-webui plugin path) and a
            // requirements_standalone.txt (no plugin). We need the second —
            // the first pulls a different torch pin and won't match the
            // venv we just built. Without this override, auto-detect would
            // pick requirements.txt and torch versioning would fight us.
            //
            // COQUI_TOS_AGREED=1: XTTS's CPML license requires explicit
            // agreement at runtime, otherwise the model load stalls at an
            // input() prompt that script.py forwards to the still-attached
            // stdin (which we can't interact with from ProcessManager). The
            // env var is the documented bypass.
            //
            // Health: AllTalk exposes /api/ready for readiness, /api/voices
            // for inventory. /api/ready is the right liveness probe — the
            // server can be up but model-loading for ~30s on first start.
            Id = "alltalk",
            DisplayName = "AllTalk TTS",
            Port = 7851,
            VramMb = 6000,
            Category = "audio",
            HealthEndpoint = "/api/ready",
            GitRepoUrl = "https://github.com/erew123/alltalk_tts.git",
            RelativeInstallPath = "alltalk",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.11",
            RequirementsFileName = "system/requirements/requirements_standalone.txt",
            PreInstallCommands = new List<string>
            {
                // Same +cu121 pinning idiom as Forge. AllTalk's
                // requirements_standalone lists `torch` unpinned, so a
                // plain pip would resolve to the CPU wheel and XTTS
                // synthesis would crawl. Pre-pin to fix the resolution.
                "-3.11 -m pip install --force-reinstall torch==2.3.1+cu121 torchaudio==2.3.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121"
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "COQUI_TOS_AGREED", "1" },
            },
            StartExecutable = "py",
            StartArguments = "-3.11 script.py",
            // Voice models live under voices/ and the XTTS model under
            // models/xtts/. ScanRoot points at the parent so both show up
            // in the Models tab.
            ModelsSubfolder = "models/xtts",
            ModelsScanRoot = "models",
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // MusicGen — facebookresearch/audiocraft, gradio demo at
            // demos/musicgen_app.py. The 0.9.2 removal was due to an empty
            // GitRepoUrl that threw NotImplementedException — we now have
            // the right entrypoint, so the install just clones the repo
            // and runs the demo.
            //
            // demos/musicgen_app.py uses gradio's default port (7860)
            // unless GRADIO_SERVER_PORT is set. We route to 7861 to keep
            // out of Forge's way.
            //
            // HEAD's-up: audiocraft pins a recent torch in their
            // requirements.txt; we still pre-pin cu121 to keep the venv
            // self-contained and not drift onto whatever the upstream
            // install command resolves on a given day.
            Id = "musicgen",
            DisplayName = "MusicGen",
            Port = 7861,
            VramMb = 8000,
            Category = "audio",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/facebookresearch/audiocraft.git",
            RelativeInstallPath = "audiocraft",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install --force-reinstall torch==2.3.1+cu121 torchaudio==2.3.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121"
            },
            PostInstallCommands = new List<string>
            {
                // audiocraft installs as a package only with `pip install -e .`
                // — without this, `python -m demos.musicgen_app` fails with
                // "ModuleNotFoundError: No module named 'audiocraft'" because
                // the requirements.txt step doesn't editable-install the
                // checked-out repo itself.
                "-m pip install -e ."
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "GRADIO_SERVER_PORT", "7861" },
                { "GRADIO_SERVER_NAME", "127.0.0.1" },
            },
            StartExecutable = "py",
            StartArguments = "-3.10 -m demos.musicgen_app",
            ModelsSubfolder = "",   // models cached in HF_HOME, not in repo dir
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // Faster-Whisper (speaches) — speech-to-text with word/segment
            // timestamps, OpenAI-compatible /v1/audio/transcriptions. Added
            // for the video-dubbing pipeline (tools/dub/dub.py): it produces
            // the timed transcript that ollama then translates and alltalk
            // re-voices in the cloned voice.
            //
            // No requirements.txt: speaches is a pyproject/uv project, so the
            // InstallerService requirements step is skipped (requirementsFile
            // stays null) and we editable-install it in PostInstallCommands
            // instead — same idiom as MusicGen's `pip install -e .`.
            //
            // No torch PreInstall: speaches transcribes via faster-whisper →
            // CTranslate2, NOT torch. CUDA acceleration comes from the
            // nvidia-cublas/cudnn cu12 wheels that the editable install pulls
            // in, so a torch pin would just bloat the venv by ~2 GB for
            // nothing. (Contrast every other service here, which IS torch.)
            //
            // Entry point: speaches ships an app factory (confirmed against
            // their Dockerfile CMD: `uvicorn --factory speaches.main:create_app`).
            // --port pins 9000 (clear of 7860/7861/7862/7863/7851/8188/11434),
            // overriding their default UVICORN_PORT=8000. Health: /health.
            // First transcription downloads the large-v3 model (~3 GB) on
            // demand, so the initial call is slow.
            //
            // Python 3.12 is REQUIRED: speaches pins `requires-python ==3.12.*`
            // in pyproject — 3.11 or 3.13 fail the editable install. py.exe
            // must therefore have a 3.12 available (`py -0` to verify).
            Id = "whisper",
            DisplayName = "Faster-Whisper",
            Port = 9000,
            VramMb = 3000,
            Category = "audio",
            HealthEndpoint = "/health",
            GitRepoUrl = "https://github.com/speaches-ai/speaches.git",
            RelativeInstallPath = "whisper",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.12",
            PostInstallCommands = new List<string>
            {
                "-m pip install -e ."
            },
            StartExecutable = "py",
            StartArguments = "-3.12 -m uvicorn --factory speaches.main:create_app --host 127.0.0.1 --port 9000",
            ModelsSubfolder = "",   // models cached in HF_HOME on first request
            UseSystemInstaller = false,
            UsesVenv = true
        },
        new ServiceDefinition
        {
            // F5-TTS — flow-matching TTS with high-fidelity, cross-lingual
            // zero-shot voice cloning. Optional quality upgrade over AllTalk's
            // XTTS for the dubbing pipeline (better long-form stability, fewer
            // hallucinations). License is CC-BY-NC — non-commercial only, fine
            // for the clan's own channel content.
            //
            // Same no-requirements.txt pattern as the whisper entry above:
            // F5-TTS is a pyproject package, so we editable-install it in
            // PostInstallCommands. torch IS needed here (the model runs on
            // torch), so we keep the cu121 PreInstall pin like the rest.
            //
            // Entry point: the repo's gradio demo lives at
            // f5_tts.infer.infer_gradio; `-m` runs its __main__. It takes
            // --port/--host. If a future release renames the module, the pip
            // console script `f5-tts_infer-gradio` is the fallback target.
            // GRADIO_SERVER_* env mirrors the TripoSR/MusicGen routing so the
            // demo binds 127.0.0.1:7863 even if the CLI flags are ignored.
            Id = "f5-tts",
            DisplayName = "F5-TTS",
            Port = 7863,
            VramMb = 4000,
            Category = "audio",
            HealthEndpoint = "/",
            GitRepoUrl = "https://github.com/SWivid/F5-TTS.git",
            RelativeInstallPath = "f5-tts",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.10",
            PreInstallCommands = new List<string>
            {
                "-3.10 -m pip install --force-reinstall torch==2.3.1+cu121 torchaudio==2.3.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121"
            },
            PostInstallCommands = new List<string>
            {
                "-m pip install -e ."
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "GRADIO_SERVER_PORT", "7863" },
                { "GRADIO_SERVER_NAME", "127.0.0.1" },
            },
            StartExecutable = "py",
            StartArguments = "-3.10 -m f5_tts.infer.infer_gradio --port 7863 --host 127.0.0.1",
            ModelsSubfolder = "",   // F5-TTS pulls checkpoints from HF on demand
            UseSystemInstaller = false,
            UsesVenv = true
        }
    };
}
