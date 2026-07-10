"""
Valerie local TTS server — F5-TTS behind a small HTTP API that mirrors the shape the C#
LocalTtsClient calls. It exists so V can speak in a cloned local voice with no cloud dependency.

POST /tts     {"text": "...", "format": "mp3"|"wav"}  -> audio bytes
GET  /health                                          -> {status, model, device, ref_audio, ...}

The model loads once at startup with a fixed speaker reference (the V voice), so every request
reuses it. Output is MP3 by default because the app's playback path (NAudio Mp3FileReader) expects
MP3; WAV is offered for debugging. MP3 encode is done with system ffmpeg (already installed) to
avoid extra Python audio deps.

Config via environment (all optional):
  V_TTS_REF_AUDIO  reference wav for the voice        (default: ./ref/voice_ref.wav)
  V_TTS_REF_TEXT   transcript of the reference clip   (default: "" -> F5 auto-transcribes once)
  V_TTS_MODEL      F5-TTS model name                  (default: F5TTS_v1_Base)
  V_TTS_SPEED      speech speed, 1.0 = natural        (default: 1.0)
  V_TTS_HOST       bind host                          (default: 127.0.0.1)
  V_TTS_PORT       bind port                          (default: 8123)
  V_TTS_SEED       fixed seed, -1 for random          (default: -1)
"""
import io
import os
import subprocess
import sys
import tempfile
import threading

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, Response
from fastapi.responses import JSONResponse
from pydantic import BaseModel

REF_AUDIO = os.environ.get("V_TTS_REF_AUDIO", os.path.join(os.path.dirname(__file__), "ref", "voice_ref.wav"))
REF_TEXT = os.environ.get("V_TTS_REF_TEXT", "")
MODEL_NAME = os.environ.get("V_TTS_MODEL", "F5TTS_v1_Base")
# Fine-tuned checkpoint + vocab (empty => download/use the base model). Set these to point at a
# trained V model, e.g. ckpts\valerie_v\model_last.safetensors — no other change needed.
MODEL_CKPT = os.environ.get("V_TTS_MODEL_CKPT", "")
MODEL_VOCAB = os.environ.get("V_TTS_VOCAB", "")
SPEED = float(os.environ.get("V_TTS_SPEED", "1.0"))
HOST = os.environ.get("V_TTS_HOST", "127.0.0.1")
PORT = int(os.environ.get("V_TTS_PORT", "8123"))
SEED = int(os.environ.get("V_TTS_SEED", "-1"))

DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

app = FastAPI(title="Valerie Local TTS (F5-TTS)")
_model = None
_sr = 24000
_lock = threading.Lock()   # F5-TTS inference is not thread-safe; serialize requests


class TtsRequest(BaseModel):
    text: str
    format: str = "mp3"


def _load_model():
    global _model, _sr
    from f5_tts.api import F5TTS  # imported lazily so --check can run without the model

    which = MODEL_CKPT if MODEL_CKPT else f"{MODEL_NAME} (base)"
    print(f"[f5] loading {which} on {DEVICE} ...", flush=True)
    _model = F5TTS(model=MODEL_NAME, ckpt_file=MODEL_CKPT, vocab_file=MODEL_VOCAB, device=DEVICE)
    if not os.path.isfile(REF_AUDIO):
        print(f"[f5] WARNING: reference audio not found: {REF_AUDIO}", flush=True)
    print(f"[f5] ready. reference: {REF_AUDIO}", flush=True)


def _to_mp3(wav: np.ndarray, sr: int) -> bytes:
    """Encode float32 mono audio to MP3 via system ffmpeg (reads WAV on stdin)."""
    wav_buf = io.BytesIO()
    sf.write(wav_buf, wav, sr, format="WAV", subtype="PCM_16")
    wav_buf.seek(0)
    proc = subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", "pipe:0",
         "-codec:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", "pipe:1"],
        input=wav_buf.read(), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"ffmpeg mp3 encode failed: {proc.stderr.decode('utf-8', 'ignore')[:300]}")
    return proc.stdout


def _to_wav(wav: np.ndarray, sr: int) -> bytes:
    buf = io.BytesIO()
    sf.write(buf, wav, sr, format="WAV", subtype="PCM_16")
    return buf.getvalue()


@app.get("/health")
def health():
    return {
        "status": "ok" if _model is not None else "loading",
        "model": MODEL_NAME,
        "checkpoint": MODEL_CKPT or "base",
        "device": DEVICE,
        "gpu": torch.cuda.get_device_name(0) if DEVICE == "cuda" else None,
        "ref_audio": REF_AUDIO,
        "ref_audio_exists": os.path.isfile(REF_AUDIO),
        "ref_text_set": bool(REF_TEXT),
        "speed": SPEED,
    }


@app.post("/tts")
def tts(req: TtsRequest):
    if _model is None:
        return JSONResponse({"error": "model still loading"}, status_code=503)
    text = (req.text or "").strip()
    if not text:
        return JSONResponse({"error": "empty text"}, status_code=400)

    try:
        with _lock:
            if SEED >= 0:
                torch.manual_seed(SEED)
            wav, sr, _ = _model.infer(
                ref_file=REF_AUDIO,
                ref_text=REF_TEXT,     # "" => F5 transcribes the reference once and caches it
                gen_text=text,
                speed=SPEED,
                remove_silence=True,
            )
        wav = np.asarray(wav, dtype=np.float32)

        if req.format.lower() == "wav":
            return Response(content=_to_wav(wav, sr), media_type="audio/wav")
        return Response(content=_to_mp3(wav, sr), media_type="audio/mpeg")
    except Exception as e:  # noqa: BLE001 — surface any inference error to the caller
        return JSONResponse({"error": f"{type(e).__name__}: {e}"}, status_code=500)


def _selftest():
    """`python server.py --check` — verify torch/CUDA and that F5-TTS imports, without serving."""
    print(f"python      : {sys.version.split()[0]}")
    print(f"torch       : {torch.__version__}")
    print(f"cuda avail  : {torch.cuda.is_available()}")
    if torch.cuda.is_available():
        print(f"gpu         : {torch.cuda.get_device_name(0)}")
        print(f"cuda build  : {torch.version.cuda}")
    try:
        import f5_tts  # noqa: F401
        print("f5_tts      : import OK")
    except Exception as e:  # noqa: BLE001
        print(f"f5_tts      : IMPORT FAILED -> {e}")
        return 1
    print(f"ref audio   : {REF_AUDIO} (exists={os.path.isfile(REF_AUDIO)})")
    return 0


if __name__ == "__main__":
    if "--check" in sys.argv:
        raise SystemExit(_selftest())

    import uvicorn
    _load_model()
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning")
