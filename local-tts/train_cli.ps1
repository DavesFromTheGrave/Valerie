# Fine-tune F5-TTS on V's dataset (valerie_v, 11,160 clips / 7.5h).
# One command:  .\train_cli.ps1
# Override batch if you hit CUDA out-of-memory:  .\train_cli.ps1 -BatchFrames 1200
#
# Checkpoints land in .venv\Lib\ckpts\valerie_v\ . Training is resumable — just re-run this.
# Stop anytime with Ctrl+C; audition a checkpoint (see README/TRAINING.md) and resume later.

param(
    [int]$BatchFrames = 1600,   # frames per GPU batch (tested 1400 & 2000 both run on the 8GB 3070 Ti). Drop to 1000 if you ever OOM.
    [int]$MaxSamples  = 32,     # max sequences per batch
    [int]$GradAccum   = 2,      # gradient accumulation (effective batch = BatchFrames * GradAccum)
    [int]$Epochs      = 40
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:HF_HOME = Join-Path $here ".cache\huggingface"   # so the base checkpoint resolves from cache
# Reduce CUDA fragmentation OOM on long runs — the real risk on an 8GB card, not batch size.
$env:PYTORCH_CUDA_ALLOC_CONF = "expandable_segments:True"

$cli = Join-Path $here ".venv\Scripts\f5-tts_finetune-cli.exe"

Write-Host "Fine-tuning F5TTS_v1_Base on valerie_v  (batch=$BatchFrames frames x$GradAccum accum, max_samples=$MaxSamples)" -ForegroundColor Cyan
Write-Host "Checkpoints -> $here\.venv\Lib\ckpts\valerie_v\   (Ctrl+C to stop; re-run to resume)" -ForegroundColor DarkGray

& $cli `
    --exp_name F5TTS_v1_Base `
    --dataset_name valerie_v `
    --tokenizer pinyin `
    --finetune `
    --learning_rate 1e-5 `
    --batch_size_per_gpu $BatchFrames `
    --batch_size_type frame `
    --max_samples $MaxSamples `
    --grad_accumulation_steps $GradAccum `
    --epochs $Epochs `
    --num_warmup_updates 2000 `
    --save_per_updates 2000 `
    --last_per_updates 1000 `
    --keep_last_n_checkpoints 5 `
    --bnb_optimizer `
    --log_samples
