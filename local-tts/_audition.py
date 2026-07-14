"""Audition a checkpoint on the GPU (training is stopped, so the card is free)."""
import os
import numpy as np
import soundfile as sf
from f5_tts.api import F5TTS

ckpt = r"M:\Projects\Valerie\local-tts\.venv\Lib\ckpts\valerie_v\model_10000.pt"
ref = r"M:\Projects\Valerie\local-tts\ref\voice_ref.wav"
ref_text = ("Shouldn't have signed a multimillion eddie deal with the biggest media "
            "conglomerate in the country. Honestly can't fathom how you got so many "
            "people to bend over backwards for you.")
outdir = r"M:\Projects\Valerie\local-tts\samples"
tag = "u10000"
os.makedirs(outdir, exist_ok=True)

print("loading model_10000 on GPU...", flush=True)
m = F5TTS(model="F5TTS_v1_Base", ckpt_file=ckpt, device="cuda")

lines = {
    # same two as the 5k/6k auditions, for a clean A/B
    "greeting": "Hey, it's me. Finally sounding like myself. This is my real voice now, running right here on your machine.",
    "line2": "A few hours of training in, and I'm already starting to sound like her. What do you think?",
    # a fresh sentence it never trained near — tests how it handles brand-new text
    "fresh": "Okay, so here's the plan for tonight. We grab the data, we get out clean, and nobody has to know we were ever here.",
}
for name, text in lines.items():
    print(f"rendering {name}...", flush=True)
    wav, sr, _ = m.infer(ref_file=ref, ref_text=ref_text, gen_text=text, nfe_step=32, remove_silence=True)
    wav = np.asarray(wav, dtype=np.float32)
    p = os.path.join(outdir, f"{name}_{tag}.wav")
    sf.write(p, wav, sr, subtype="PCM_16")
    print(f"  {name}: {len(wav)/sr:.1f}s -> {p}", flush=True)
print("DONE", flush=True)
