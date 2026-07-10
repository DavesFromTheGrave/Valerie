# Launch the F5-TTS fine-tuning UI (transcribe -> prepare -> train, all in one browser page).
# Usage:  .\train.ps1     then open the http://127.0.0.1:7860 URL it prints.
#
# This does NOT start training by itself — it opens the tool. You click through the steps.
# Stop the local voice server first if it's running (both want the GPU).

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Reuse the in-project model cache (base checkpoint downloads here, not the user profile).
$env:HF_HOME = Join-Path $here ".cache\huggingface"

$gradio = Join-Path $here ".venv\Scripts\f5-tts_finetune-gradio.exe"
Write-Host "Opening the F5-TTS fine-tune UI. Watch the console for the http://127.0.0.1:7860 URL." -ForegroundColor Cyan
Write-Host "Tip: keep this window open — training logs and loss print here." -ForegroundColor DarkGray
& $gradio
