# Fine-tuning V's voice (F5-TTS)

Turns the zero-shot bridge into the real clone. **The slow prep is already done and verified** —
you just launch training when your GPU is free.

## Status (what's ready)
- **Dataset built:** `dataset\valerie_v\` — 11,160 clips converted to 24kHz mono WAV,
  loudness-normalized, silence-trimmed, and transcribed with faster-whisper large-v3.
- **F5 dataset prepared:** `.venv\Lib\data\valerie_v_pinyin\` (raw.arrow + duration.json +
  the base model's 2545-token vocab). **7.50 hours** of clean speech. Verified to load.
- **Training smoke-tested:** the exact command below reached the training loop on your 3070 Ti
  with correct vocab and no OOM. Checkpoints go to `.venv\Lib\ckpts\valerie_v\`.

---

## Start training (one command)

```powershell
cd M:\Projects\Valerie\local-tts
.\train_cli.ps1
```

That's it. It fine-tunes `F5TTS_v1_Base` on the V dataset with settings tuned for your 8GB card
(batch 1600 frames, 8-bit Adam via bitsandbytes, expandable CUDA segments to avoid fragmentation
OOM). Loss prints in the window.

- **Resumable:** Ctrl+C anytime, re-run `.\train_cli.ps1` — it continues from the last checkpoint.
- **If you ever OOM:** `.\train_cli.ps1 -BatchFrames 1000` (slower, less memory).
- **Speed reality:** ~1000 optimizer updates/epoch on 8GB. This is an overnight-to-days job, which
  is why it checkpoints often — you don't wait for "done," you audition and stop when she sounds right.

Checkpoints save every 2000 updates (plus a rolling "last" every 1000), into
`.venv\Lib\ckpts\valerie_v\`. `--log_samples` also drops test audio in `ckpts\valerie_v\samples\`.

---

## Audition a checkpoint mid-training

You don't need to stop training. Point the voice server at a saved checkpoint in a *second*
terminal (or after stopping). In `run.ps1`, uncomment and set:

```powershell
$env:V_TTS_MODEL_CKPT = Join-Path $here ".venv\Lib\ckpts\valerie_v\model_last.pt"
```

Run `run.ps1`, and V speaks through the fine-tuned checkpoint — same app path, no rebuild.
`/health` shows which checkpoint loaded. When a checkpoint sounds right, that's your model; stop
training. (Use the exact filename that appears in the ckpts folder — F5 writes `model_last.pt`
and `model_<step>.pt`.)

---

## Deploy the finished voice
Set `V_TTS_MODEL_CKPT` in `run.ps1` (as above) to your chosen checkpoint and start the server.
The C# app is unchanged — with `LocalTts.Prefer = true` it already routes through this server.

---

## Rebuilding the dataset (only if you want to re-curate)
`build_dataset.py` reads `_selected.txt` (the curated clip list), converts + transcribes, and
writes `dataset\valerie_v\metadata.csv`. To change the cut, regenerate `_selected.txt` from
`..\voice_raw_full_scan.csv` and re-run, then re-run F5's `prepare_csv_wavs.py` (see git history
of this file / the commands used). Current cut: dropped possessed-effect lines, sub-1.8s barks,
and clipped clips; kept 11,160 clean conversational lines.

## Gradio UI alternative
`.\train.ps1` opens F5's fine-tune UI (http://127.0.0.1:7860) if you'd rather click than use the
CLI. The dataset is already prepared as project **valerie_v** (tokenizer: pinyin), so you can skip
its transcribe/prepare tabs and go straight to the Train tab.
