# tensorlay-whisper

Minimal [faster-whisper](https://github.com/SYSTRAN/faster-whisper) STT server with
an OpenAI-compatible `POST /v1/audio/transcriptions` endpoint. Built for the
[TensorLay](https://github.com/papergallery/tensorlay) video-dubbing pipeline, but
usable standalone.

It exists to replace [speaches](https://github.com/speaches-ai/speaches) in that
pipeline: speaches is an unpinned kitchen-sink (STT + Kokoro/Piper TTS + pyannote +
aiortc) whose dependency surface rots against current PyPI — its `onnx_asr` import
broke at startup. This server exposes *only* the one endpoint the pipeline calls, on
top of `faster-whisper` + `CTranslate2`, both of which ship binary wheels (no
compiler needed).

## Install & run

```bash
python -m venv .venv && . .venv/Scripts/activate   # Windows; use bin/activate on Linux
pip install -r requirements.txt
python -m uvicorn server:app --host 127.0.0.1 --port 9000
```

Python 3.10–3.12 (CTranslate2 4.7.2 ships wheels up to cp312).

First request downloads the model (`Systran/faster-whisper-large-v3`, ~3 GB) into the
HuggingFace cache, then it is reused.

## Endpoints

- `GET /health` → `{"status": "ok", "model": "..."}`
- `POST /v1/audio/transcriptions` (multipart) — fields:
  - `file` (required) — the audio file
  - `language` (optional) — ISO code; auto-detected when omitted
  - `model`, `response_format` — accepted for OpenAI compatibility, otherwise ignored

  Returns `{"task", "language", "duration", "text", "segments": [{id, start, end, text}]}`.
  The pipeline reads `segments`.

## Configuration (env vars)

| Var | Default | Notes |
|---|---|---|
| `WHISPER_MODEL` | `Systran/faster-whisper-large-v3` | any faster-whisper / CT2 model id |
| `WHISPER_DEVICE` | `auto` | `auto` \| `cuda` \| `cpu` |
| `WHISPER_COMPUTE` | `default` | `default` → float16 on cuda, int8 on cpu |

The server tries the GPU first and **falls back to CPU int8** if CUDA is missing or
broken — each backend is warmed up at startup with 1 s of silence, so a missing
cuDNN/cuBLAS DLL fails fast at boot rather than on the first real transcription.

## License

MIT.
