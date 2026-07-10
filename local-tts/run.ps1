# Launch Valerie's local F5-TTS voice server.
# Usage:  .\run.ps1
# The C# app talks to http://127.0.0.1:8123 when LocalTts.Prefer = true in appsettings.json.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Route the HuggingFace model cache into the project (keeps it off the user profile).
$env:HF_HOME = Join-Path $here ".cache\huggingface"

# The V voice reference clip + its transcript. Providing the text skips the per-request
# Whisper transcription pass entirely (big latency win). If you rebuild the reference,
# update this text to match what the new clip actually says.
$env:V_TTS_REF_AUDIO = Join-Path $here "ref\voice_ref.wav"
$env:V_TTS_REF_TEXT  = "Shouldn't have signed a multimillion eddie deal with the biggest media conglomerate in the country. Honestly can't fathom how you got so many people to bend over backwards for you."

# Fine-tuned model: once training produces a checkpoint, point here and V speaks in the
# fine-tuned voice through the same path. Leave commented to use the base (zero-shot) model.
# $env:V_TTS_MODEL_CKPT = Join-Path $here "ckpts\valerie_v\model_last.safetensors"

# Voice tuning
$env:V_TTS_SPEED = "0.95"     # 1.0 = natural; slightly under is often warmer
$env:V_TTS_HOST  = "127.0.0.1"
$env:V_TTS_PORT  = "8123"

$py = Join-Path $here ".venv\Scripts\python.exe"
Write-Host "Starting F5-TTS server on http://$($env:V_TTS_HOST):$($env:V_TTS_PORT) ..." -ForegroundColor Cyan
& $py (Join-Path $here "server.py")
