"""Fair A/B: identical line, identical speed — only the model differs (base vs fine-tune)."""
import os
import numpy as np
import soundfile as sf
from f5_tts.api import F5TTS

ref = r"M:\Projects\Valerie\local-tts\ref\voice_ref.wav"
ref_text = ("Shouldn't have signed a multimillion eddie deal with the biggest media "
            "conglomerate in the country. Honestly can't fathom how you got so many "
            "people to bend over backwards for you.")
# the exact "longer" line that v_longer_s09 used
line = ("I know it took a while to get here. But this is the whole point, isn't it? "
        "A voice that's actually mine, that lives on your hardware and answers to you and nobody else.")
outdir = r"M:\Projects\Valerie\local-tts\samples"
os.makedirs(outdir, exist_ok=True)
ckpt10k = r"M:\Projects\Valerie\local-tts\.venv\Lib\ckpts\valerie_v\model_10000.pt"

for label, ckpt in [("BASE_untrained", ""), ("FINETUNE_10k", ckpt10k)]:
    print(f"loading {label}...", flush=True)
    m = F5TTS(model="F5TTS_v1_Base", ckpt_file=ckpt, device="cuda")
    wav, sr, _ = m.infer(ref_file=ref, ref_text=ref_text, gen_text=line,
                         speed=0.9, nfe_step=32, remove_silence=True)
    wav = np.asarray(wav, dtype=np.float32)
    p = os.path.join(outdir, f"AB_longer_s09_{label}.wav")
    sf.write(p, wav, sr, subtype="PCM_16")
    print(f"  -> {p} ({len(wav)/sr:.1f}s)", flush=True)
    del m
print("DONE", flush=True)
