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
            //
            // WINDOWS-WHEEL FIX (RequirementsReplacements below): AllTalk's
            // requirements_standalone.txt pins `TTS==0.22.0` (coqui, archived)
            // and `av==11.0.0`, NEITHER of which has a Windows wheel — TTS
            // compiles a Cython monotonic_align ext and av builds PyAV from
            // source, both needing MSVC C++ Build Tools the user doesn't have
            // (the install died exactly here). We swap TTS -> coqui-tts==0.25.3
            // (the maintained idiap fork; first pure-wheel release, uses the
            // wheeled monotonic-alignment-search instead of compiling) and
            // av -> av>=12 (first av with win wheels, pulled via faster-whisper
            // which we also bump to 1.2.1 since 1.0.1 pinned av==11). coqui-tts
            // 0.25.3's newer floors then force a small cascade: transformers
            // 4.39.1 -> 4.46.2 (still <5 so it doesn't disable torch 2.3.1),
            // num2words/gruut/tensorboard bumps, and dropping the now-stale
            // tokenizers/huggingface-hub/gruut-lang/trainer pins so the
            // resolver picks coherent versions. Verified end-to-end on a VPS
            // py3.11 venv: resolves, installs, `from TTS.api import TTS` +
            // XTTS model class import OK, torch stays available. All bumped
            // packages have Windows wheels (or are pure-python).
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
            RequirementsReplacements = new Dictionary<string, string>
            {
                // swaps (no Windows wheel -> maintained drop-in)
                { "tts",            "coqui-tts==0.25.3" },
                { "av",             "av>=12.0.0,<13" },
                { "faster-whisper", "faster-whisper==1.2.1" },
                // cascade bumps coqui-tts 0.25.3 / transformers 4.46 require
                { "transformers",   "transformers==4.46.2" },
                { "num2words",      "num2words==0.5.14" },
                { "gruut",          "gruut>=2.4.0" },
                { "tensorboard",    "tensorboard>=2.17.0" },
                // stale pins dropped -> resolver picks coherent transitive versions
                { "tokenizers",     "" },
                { "huggingface-hub","" },
                { "gruut-ipa",      "" },
                { "gruut-lang-de",  "" },
                { "gruut-lang-en",  "" },
                { "gruut-lang-es",  "" },
                { "gruut-lang-fr",  "" },
                { "trainer",        "" },
            },
            PreInstallCommands = new List<string>
            {
                // Same +cu121 pinning idiom as Forge. AllTalk's
                // requirements_standalone lists `torch` unpinned, so a
                // plain pip would resolve to the CPU wheel and XTTS
                // synthesis would crawl. Pre-pin to fix the resolution.
                "-3.11 -m pip install --force-reinstall torch==2.3.1+cu121 torchaudio==2.3.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121",
                // setuptools<81 keeps pkg_resources available — coqui-tts
                // 0.25.3 imports it at runtime, and setuptools >=81 removed it
                // (import TTS.api would crash with ModuleNotFoundError).
                "-3.11 -m pip install \"setuptools<81\""
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
                // audiocraft is hard-pinned to the torch 2.1.0 family (torch==2.1.0,
                // torchvision==0.16.0, torchtext==0.16.0, xformers<0.0.23). The old
                // torch 2.3.1 pin here was wrong — the requirements step downgraded
                // it back to 2.1.0 anyway, leaving an incoherent env. Install the
                // 2.1.0 CUDA stack directly so requirements.txt is satisfied as-is.
                "-3.10 -m pip install --force-reinstall torch==2.1.0+cu121 torchaudio==2.1.0+cu121 torchvision==0.16.0+cu121 --extra-index-url https://download.pytorch.org/whl/cu121",
                // Relax three upstream pins that no longer resolve cleanly in 2026.
                // setup.py reads requirements.txt for install_requires, so this single
                // rewrite fixes BOTH the `pip install -r` step and the editable
                // `pip install -e .` post-step.
                //  • av==11.0.0 is the ONE av release with no Windows wheel → forces a
                //    source build that needs MSVC. 12.x/13.x ship cp310 win wheels.
                //  • transformers/gradio are upper-unbounded, so a fresh install pulls
                //    transformers 5.x + gradio 6.x, which break audiocraft's Encodec
                //    integration and its musicgen_app.py (gr.make_waveform + the
                //    gradio-4 `sources=[...]` Audio API). Cap both to the last
                //    compatible majors. The gradio range is the one pin not verifiable
                //    from a Linux build host — narrow it from an install test if the
                //    demo still errors on a gradio API mismatch.
                "-3.10 -c \"f='requirements.txt';s=open(f,encoding='utf-8').read();s=s.replace('av==11.0.0','av>=12,<14').replace('transformers>=4.31.0','transformers>=4.31.0,<4.41').replace('gradio','gradio>=4.0,<4.37');open(f,'w',encoding='utf-8').write(s)\""
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
            // Faster-Whisper — speech-to-text with word/segment timestamps,
            // OpenAI-compatible /v1/audio/transcriptions. Added for the
            // video-dubbing pipeline (tools/dub/dub.py): it produces the timed
            // transcript that ollama then translates and alltalk/f5-tts
            // re-voices in the cloned voice.
            //
            // Backend: github.com/papergallery/tensorlay-whisper — a ~60-line
            // FastAPI server over faster-whisper + CTranslate2 (mirror of
            // tools/whisper-server/ in this repo). It REPLACES speaches, whose
            // unpinned kitchen-sink deps rotted against PyPI (onnx_asr import
            // broke at startup). The repo ships a plain requirements.txt at
            // root, so the InstallerService requirements step installs it
            // automatically — no editable/PostInstall step.
            //
            // No torch PreInstall: transcription runs on faster-whisper →
            // CTranslate2, NOT torch. CUDA acceleration comes from the
            // nvidia-cublas/cudnn cu12 wheels CT2 pulls in. The server warms
            // up the GPU at startup and falls back to CPU int8 if CUDA is
            // missing/broken, so it never crashes on a box without a working
            // CUDA runtime. (Contrast every other service here, which IS torch.)
            //
            // Entry point: uvicorn server:app (server.py lives at repo root).
            // --port pins 9000 (clear of 7860/7861/7862/7863/7851/8188/11434).
            // Health: /health. First transcription downloads large-v3 (~3 GB)
            // on demand, so the initial call is slow.
            //
            // Python 3.12 pin: CTranslate2 4.7.2 ships wheels up to cp312, so
            // we keep -3.12 to guarantee a wheel exists (a 3.13 box would have
            // none). py.exe must have a 3.12 available (`py -0` to verify).
            Id = "whisper",
            DisplayName = "Faster-Whisper",
            Port = 9000,
            VramMb = 3000,
            Category = "audio",
            HealthEndpoint = "/health",
            GitRepoUrl = "https://github.com/papergallery/tensorlay-whisper.git",
            RelativeInstallPath = "whisper",
            PythonExecutable = "py",
            PythonInterpreterArgs = "-3.12",
            StartExecutable = "py",
            StartArguments = "-3.12 -m uvicorn server:app --host 127.0.0.1 --port 9000",
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
            // PINNED to f5-tts==1.1.9 (the last release BEFORE the torchcodec
            // dependency landed — torchcodec was added in 1.1.15). This is
            // deliberate: F5-TTS main now hard-depends on `torchcodec`, whose
            // first Windows wheel is 0.7.0 and that in turn needs torch 2.8
            // (no cu121 build exists) PLUS system FFmpeg shared DLLs on PATH —
            // an external dependency pip can't satisfy. Pinning 1.1.9 keeps the
            // whole thing on our coherent torch 2.3.1+cu121 stack with zero
            // torchcodec and zero FFmpeg requirement. Verified end-to-end on
            // the VPS: the dep graph resolves with NO torchcodec and
            // `import f5_tts.infer.infer_gradio` succeeds.
            //
            // We still git-clone the repo (so the installer's no-GitRepoUrl
            // early-return doesn't skip the whole Python pipeline), but install
            // the PINNED PyPI wheel in PostInstall instead of `pip install -e .`
            // off the clone. F5-TTS is src-layout (src/f5_tts), so the cloned
            // main tree does NOT shadow the installed 1.1.9 at runtime when
            // `-m f5_tts...` runs from the clone root.
            //
            // The PreInstall pins matter: 1.1.9's own constraints are loose
            // (gradio>=5.0.0, transformers unbounded, numpy uncapped on >3.10),
            // so without these pip would pull gradio 6 (API break vs 1.1.9's
            // demo), transformers 5.x (which DISABLES torch <2.4 — silently
            // breaks model loading on our 2.3.1), and numpy 2 (torch 2.3.1 was
            // built against numpy 1). Pinning them first makes the f5-tts
            // install keep these versions. All have Windows wheels (verified:
            // bitsandbytes 0.49.2, torch/torchaudio 2.3.1+cu121 cp310 win).
            //
            // Entry point: the gradio demo lives at f5_tts.infer.infer_gradio;
            // `-m` runs its __main__. It takes --port/--host. GRADIO_SERVER_*
            // env mirrors the TripoSR/MusicGen routing so the demo binds
            // 127.0.0.1:7863 even if the CLI flags are ignored.
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
                "-3.10 -m pip install --force-reinstall torch==2.3.1+cu121 torchaudio==2.3.1+cu121 --extra-index-url https://download.pytorch.org/whl/cu121",
                "-3.10 -m pip install \"numpy<2\" \"transformers>=4.40,<5\" \"gradio>=5.0,<6\""
            },
            PostInstallCommands = new List<string>
            {
                "-m pip install \"f5-tts==1.1.9\""
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
