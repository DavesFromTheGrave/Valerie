#!/usr/bin/env python3
import os
os.environ["ESPEAK_LIBRARY"] = "/usr/lib/x86_64-linux-gnu/libespeak-ng.so.1"
os.environ["PHONEMIZER_ESPEAK_PATH"] = "/usr/bin/espeak-ng"
os.environ["PATH"] = "/usr/bin:" + os.environ.get("PATH", "")

from phonemizer.backend.espeak.wrapper import EspeakWrapper
EspeakWrapper.set_library("/usr/lib/x86_64-linux-gnu/libespeak-ng.so.1")

import sys, io, wave, asyncio, logging
import numpy as np

_STYLETTS2   = "/workspace/StyleTTS2"
_MODEL_DIR   = "/workspace/tts_models"
_CONFIG_PATH = _MODEL_DIR + "/config.yml"
_CKPT_PATH   = _MODEL_DIR + "/epochs_2nd_00020.pth"
_REF_WAV     = "/workspace/voice_reference/reference.wav"
_VOICE_DATA  = "/workspace/voice_data"

sys.path.insert(0, _STYLETTS2)
os.chdir(_STYLETTS2)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("tts_server")

import torch, torchaudio, librosa, yaml
from collections import OrderedDict
from models import build_model, load_F0_models, load_ASR_models
from utils import recursive_munch, length_to_mask
from text_utils import TextCleaner
from Utils.PLBERT.util import load_plbert
from Modules.diffusion.sampler import DiffusionSampler, ADPM2Sampler, KarrasSchedule

try:
    import phonemizer as _pm
    _phonemizer = _pm.backend.EspeakBackend("en-us", preserve_punctuation=True, with_stress=True)
    log.info("phonemizer ready.")
except Exception as e:
    _phonemizer = None
    log.error(f"phonemizer failed: {e}")

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel
from contextlib import asynccontextmanager
import uvicorn

SAMPLE_RATE = 24000
device      = "cuda" if torch.cuda.is_available() else "cpu"
_model = _sampler = _cleaner = _ref_style = None

_to_mel = torchaudio.transforms.MelSpectrogram(n_mels=80, n_fft=2048, win_length=1200, hop_length=300)


def _preprocess(wave):
    t   = torch.from_numpy(wave).float()
    mel = _to_mel(t)
    return (torch.log(1e-5 + mel.unsqueeze(0)) - (-4)) / 4


def _compute_style(wav_path):
    audio, _ = librosa.load(wav_path, sr=SAMPLE_RATE)
    audio, _ = librosa.effects.trim(audio, top_db=30)
    mel      = _preprocess(audio).to(device)
    with torch.no_grad():
        ref_s = _model.style_encoder(mel.unsqueeze(1))
        ref_p = _model.predictor_encoder(mel.unsqueeze(1))
    return torch.cat([ref_s, ref_p], dim=1)


def _compute_style_from_dir(dir_path):
    exts  = {".wav", ".mp3", ".flac", ".ogg", ".m4a"}
    files = sorted(os.path.join(dir_path, f) for f in os.listdir(dir_path)
                   if os.path.splitext(f)[1].lower() in exts)
    if not files:
        raise ValueError(f"No audio in {dir_path}")
    log.info(f"Computing style from {len(files)} clips …")
    embeddings = []
    for i, path in enumerate(files):
        try:
            audio, _ = librosa.load(path, sr=SAMPLE_RATE)
            audio, _ = librosa.effects.trim(audio, top_db=30)
            if len(audio) < 512:
                continue
            mel = _preprocess(audio).to(device)
            with torch.no_grad():
                ref_s = _model.style_encoder(mel.unsqueeze(1))
                ref_p = _model.predictor_encoder(mel.unsqueeze(1))
            embeddings.append(torch.cat([ref_s, ref_p], dim=1))
        except Exception as e:
            log.warning(f"  Skipped {os.path.basename(path)}: {e}")
        if (i + 1) % 10 == 0:
            log.info(f"  {i+1}/{len(files)} done …")
    if not embeddings:
        raise ValueError("No valid clips.")
    avg = torch.stack([e.squeeze(0) for e in embeddings]).mean(dim=0, keepdim=True)
    log.info(f"Style averaged from {len(embeddings)} clips.")
    return avg


_VOICE_DATA_ASH = "/workspace/voice_data_ash"

# voice profile registry: name -> style tensor (loaded at startup)
_voices: dict = {}


def _load_model():
    global _model, _sampler, _cleaner, _ref_style
    log.info(f"Loading StyleTTS2 (device={device}) …")
    cfg = yaml.safe_load(open(_CONFIG_PATH))

    text_aligner    = load_ASR_models(cfg["ASR_path"], cfg["ASR_config"])
    pitch_extractor = load_F0_models(cfg["F0_path"])
    plbert          = load_plbert(cfg["PLBERT_dir"])

    params = recursive_munch(cfg["model_params"])
    _model = build_model(params, text_aligner, pitch_extractor, plbert)
    for k in _model:
        _model[k].eval().to(device)

    ckpt = torch.load(_CKPT_PATH, map_location="cpu", weights_only=False)["net"]
    for key in _model:
        if key not in ckpt:
            continue
        try:
            _model[key].load_state_dict(ckpt[key])
        except Exception:
            sd = OrderedDict((k[7:] if k.startswith("module.") else k, v)
                             for k, v in ckpt[key].items())
            _model[key].load_state_dict(sd, strict=False)
    for k in _model:
        _model[k].eval()

    _sampler = DiffusionSampler(
        _model.diffusion.diffusion,
        sampler=ADPM2Sampler(),
        sigma_schedule=KarrasSchedule(sigma_min=0.0001, sigma_max=3.0, rho=9.0),
        clamp=False,
    )
    _cleaner = TextCleaner()

    # Load default voice (CP2077 V)
    if os.path.isfile(_REF_WAV):
        _ref_style = _compute_style(_REF_WAV)
        _voices["default"] = _ref_style
        log.info("Reference WAV loaded as default voice.")
    elif os.path.isdir(_VOICE_DATA) and os.listdir(_VOICE_DATA):
        _ref_style = _compute_style_from_dir(_VOICE_DATA)
        _voices["default"] = _ref_style
        log.info("CP2077 V voice loaded as default.")
    else:
        log.info("No default voice reference — using pretrained voice.")

    # Load Ash's voice if available
    if os.path.isdir(_VOICE_DATA_ASH) and os.listdir(_VOICE_DATA_ASH):
        log.info("Loading Ash voice profile …")
        _voices["ash"] = _compute_style_from_dir(_VOICE_DATA_ASH)
        log.info("Ash voice loaded.")

    log.info(f"StyleTTS2 ready. Voices: {list(_voices.keys()) or ['pretrained']}")


def _synthesize(text, diffusion_steps=10, embedding_scale=1.0, voice="default"):
    if _phonemizer is None:
        raise RuntimeError("espeak-ng / phonemizer not available.")

    ref_style = _voices.get(voice) or _ref_style

    text = text.strip().replace('"', '')
    ps   = _phonemizer.phonemize([text])[0].strip()

    tokens = _cleaner(ps)
    tokens.insert(0, 0)
    tokens = torch.LongTensor(tokens).to(device).unsqueeze(0)

    with torch.no_grad():
        input_lengths = torch.LongTensor([tokens.shape[-1]]).to(device)
        text_mask     = length_to_mask(input_lengths).to(device)

        t_en     = _model.text_encoder(tokens, input_lengths, text_mask)
        bert_dur = _model.bert(tokens, attention_mask=(~text_mask).int())
        d_en     = _model.bert_encoder(bert_dur).transpose(-1, -2)

        noise    = torch.randn(1, 1, 256).to(device)
        features = ref_style.to(device) if ref_style is not None else None
        s_pred   = _sampler(
            noise,
            embedding=bert_dur,
            features=features,
            num_steps=10,
            embedding_scale=embedding_scale,
        ).squeeze(1)

        if ref_style is not None:
            ref = 0.3 * s_pred[:, :128] + 0.7 * ref_style[:, :128].to(device)
            s   = 0.7 * s_pred[:, 128:] + 0.3 * ref_style[:, 128:].to(device)
        else:
            ref = s_pred[:, :128]
            s   = s_pred[:, 128:]

        d        = _model.predictor.text_encoder(d_en, s, input_lengths, text_mask)
        x, _     = _model.predictor.lstm(d)
        duration = torch.sigmoid(_model.predictor.duration_proj(x)).sum(axis=-1)
        pred_dur = torch.round(duration.squeeze()).clamp(min=1)
        if pred_dur.dim() == 0:
            pred_dur = pred_dur.unsqueeze(0)
        pred_dur[-1] += 5

        aln = torch.zeros(input_lengths, int(pred_dur.sum().data))
        c   = 0
        for i in range(aln.size(0)):
            aln[i, c:c + int(pred_dur[i].data)] = 1
            c += int(pred_dur[i].data)
        aln = aln.unsqueeze(0).to(device)

        en             = d.transpose(-1, -2) @ aln
        F0_pred, N_pred = _model.predictor.F0Ntrain(en, s)
        out            = _model.decoder(t_en @ aln, F0_pred, N_pred, ref.squeeze().unsqueeze(0))

    audio   = out.squeeze().cpu().numpy()
    pcm     = (np.clip(audio, -1.0, 1.0) * 32767).astype(np.int16)
    buf     = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(SAMPLE_RATE)
        wf.writeframes(pcm.tobytes())
    return buf.getvalue()


@asynccontextmanager
async def lifespan(app):
    _load_model()
    yield

app      = FastAPI(title="Valerie TTS", lifespan=lifespan)
_aio_lock = asyncio.Lock()


class TTSRequest(BaseModel):
    text: str
    voice: str = "default"


@app.post("/tts")
async def tts_post(req: TTSRequest):
    if not req.text.strip():
        raise HTTPException(400, "empty text")
    try:
        async with _aio_lock:
            loop  = asyncio.get_event_loop()
            audio = await loop.run_in_executor(None, _synthesize, req.text, 10, 1.0, req.voice)
        return Response(content=audio, media_type="audio/wav")
    except Exception as e:
        log.exception("synthesis error")
        raise HTTPException(500, str(e))


@app.get("/tts")
async def tts_get(text: str, voice: str = "default"):
    if not text.strip():
        raise HTTPException(400, "empty text")
    try:
        async with _aio_lock:
            loop  = asyncio.get_event_loop()
            audio = await loop.run_in_executor(None, _synthesize, text, 10, 1.0, voice)
        return Response(content=audio, media_type="audio/wav")
    except Exception as e:
        log.exception("synthesis error")
        raise HTTPException(500, str(e))


@app.get("/voices")
async def list_voices():
    return {"voices": list(_voices.keys()) or ["pretrained"]}


@app.get("/health")
async def health():
    return {"status": "ok", "device": device, "voice_cloning": _ref_style is not None}


if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8190)
