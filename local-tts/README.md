# Valerie Local Voice (F5-TTS)

V's offline, owns-the-voice TTS path. A small FastAPI server wraps [F5-TTS](https://github.com/SWivid/F5-TTS)
and exposes `POST /tts`, which the C# app calls through `LocalTtsClient` when
`LocalTts.Prefer = true` in `appsettings.json` and the server is reachable. If it isn't running,
the app silently falls back to Grok (Ara) — nothing breaks.

## What's here
- `server.py` — the HTTP server (`/tts`, `/health`; `--check` for a torch/CUDA/F5 self-test)
- `run.ps1` — launcher; sets the model cache + reference clip + transcript, then starts the server
- `ref/voice_ref.wav` — the V voice reference (9.25s, built from the cleanest game voicelines)
- `requirements.txt` — Python deps (install torch separately first, see below)
- `samples/` — generated audio auditions (gitignore-able)

## Setup (already done on this machine — recorded for a fresh clone)
Base Python: `C:\Users\Dave\AppData\Local\Programs\Python\Python311` (3.11.9).

```powershell
cd M:\Projects\Valerie\local-tts
& "C:\Users\Dave\AppData\Local\Programs\Python\Python311\python.exe" -m venv .venv
.\.venv\Scripts\python -m pip install --upgrade pip
# 1) Torch WITH CUDA (matches the 3070 Ti / driver). Must come from the CUDA index:
.\.venv\Scripts\python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
# 2) F5-TTS + server deps:
.\.venv\Scripts\python -m pip install -r requirements.txt
# 3) IMPORTANT — remove torchcodec (see gotcha below):
.\.venv\Scripts\python -m pip uninstall -y torchcodec
```

## The torchcodec gotcha (why step 3 matters)
F5-TTS loads audio via `torchaudio`, and torchaudio 2.6 defaults to the **torchcodec** backend.
On Windows torchcodec needs FFmpeg's *shared* DLLs (the "full-shared" build). The FFmpeg on this
machine is the gyan.dev "full_build" — a **static** build with no DLLs — so torchcodec fails to
load its `libtorchcodec_core*.dll` at inference time (HTTP 500, `Could not load libtorchcodec`).

Fix used here: **uninstall torchcodec**. torchaudio then falls back to the `soundfile` backend,
which reads the WAV reference perfectly. (Alternative, not used: install the FFmpeg "full-shared"
build and put its DLLs on PATH.) System FFmpeg is still required — the server shells out to
`ffmpeg.exe` to encode MP3, which the static build does fine.

## Run
```powershell
.\run.ps1
```
First launch downloads the F5-TTS model (~1.3 GB) into `.cache/huggingface` (kept in-project).
Then in `appsettings.json` set `LocalTts.Prefer` to `true` and start Valerie — the header will
read `Voice : Local F5-TTS @ http://127.0.0.1:8123`.

## Reference clip + transcript
`run.ps1` passes `V_TTS_REF_TEXT` — the exact transcript of `ref/voice_ref.wav`. Providing it
skips a Whisper transcription pass on every request (big latency win). **If you rebuild the
reference clip, update that transcript to match** or quality/latency will suffer.

## The endgame: fine-tuning
This zero-shot setup is the bridge. The real clone is an F5-TTS fine-tune — and the dataset is
**already built**: 11,160 curated clips / 7.5h, transcribed and prepared as `valerie_v`. Launch
training with `.\train_cli.ps1`. Full guide in **TRAINING.md**.
