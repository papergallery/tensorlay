"""Minimal faster-whisper STT server — OpenAI-compatible /v1/audio/transcriptions.

Replaces the speaches kitchen-sink (STT+TTS+diarization+realtime), whose unpinned
deps rot against current PyPI (onnx_asr API drift broke import at start). This
exposes exactly the endpoint tools/dub/dub.py calls and nothing else, on top of
faster-whisper + CTranslate2 — both ship binary wheels, so no compiler is needed.
"""
import os
import tempfile

import numpy as np
from fastapi import FastAPI, File, Form, UploadFile
from faster_whisper import WhisperModel

app = FastAPI(title="tensorlay-whisper")

_model = None
_MODEL_NAME = os.environ.get("WHISPER_MODEL", "Systran/faster-whisper-large-v3")
_DEVICE = os.environ.get("WHISPER_DEVICE", "auto")          # auto picks cuda when present
_COMPUTE = os.environ.get("WHISPER_COMPUTE", "default")     # float16 on cuda, int8 on cpu


def _build_model():
    # Try GPU first, then fall back to CPU int8 so the server never dies on a
    # box where CUDA is absent OR present-but-broken. On Windows the GPU path
    # constructs fine yet throws at the first inference when cuDNN/cuBLAS DLLs
    # are missing, so each attempt does a 1s-of-silence warmup to surface that
    # failure here (at startup) instead of on the user's first transcription.
    attempts = []
    if _DEVICE in ("auto", "cuda"):
        attempts.append(("cuda", "float16" if _COMPUTE == "default" else _COMPUTE))
    attempts.append(("cpu", "int8" if _COMPUTE == "default" else _COMPUTE))

    last_err = None
    for device, compute in attempts:
        try:
            m = WhisperModel(_MODEL_NAME, device=device, compute_type=compute)
            list(m.transcribe(np.zeros(16000, dtype=np.float32))[0])  # force kernel load
            print(f"[whisper] model ready on {device}/{compute}", flush=True)
            return m
        except Exception as e:  # noqa: BLE001 — any CUDA/DLL/load error → next backend
            print(f"[whisper] {device}/{compute} unavailable: {e!r}", flush=True)
            last_err = e
    raise RuntimeError(f"no usable whisper backend (last error: {last_err!r})")


def _get_model():
    global _model
    if _model is None:
        _model = _build_model()
    return _model


@app.get("/health")
def health():
    return {"status": "ok", "model": _MODEL_NAME}


@app.post("/v1/audio/transcriptions")
async def transcriptions(
    file: UploadFile = File(...),
    model: str = Form(None),
    language: str = Form(None),
    response_format: str = Form("verbose_json"),
):
    suffix = os.path.splitext(file.filename or "audio.wav")[1] or ".wav"
    fd, path = tempfile.mkstemp(suffix=suffix)
    try:
        with os.fdopen(fd, "wb") as out:
            out.write(await file.read())
        segments, info = _get_model().transcribe(path, language=language or None)
        segs = [
            {"id": i, "start": round(s.start, 3), "end": round(s.end, 3), "text": s.text}
            for i, s in enumerate(segments)
        ]
    finally:
        os.unlink(path)
    return {
        "task": "transcribe",
        "language": language or getattr(info, "language", None),
        "duration": getattr(info, "duration", None),
        "text": "".join(s["text"] for s in segs),
        "segments": segs,
    }
