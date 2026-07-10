"""
Build the F5-TTS fine-tune dataset for V:
  1. Take the curated clip list (_selected.txt)
  2. Convert each source .Mp3 -> 24kHz mono 16-bit WAV, loudness-normalized + silence-trimmed
  3. Transcribe each with faster-whisper large-v3
  4. Write metadata.csv in F5 format:  audio_file|text  (absolute wav path)

Resumable: clips already present in metadata.csv are skipped. Safe to re-run after an interrupt.
Set env BUILD_LIMIT=N to process only the first N (for a quick test).
"""
import os
import subprocess
import time

from faster_whisper import WhisperModel

RAW_DIRS = [
    r"M:\V-Prompts\Female V Raw Voicelines (Includes Phantom Liberty)\raw\base\localization\en-us\vo",
    r"M:\V-Prompts\Female V Raw Voicelines (Includes Phantom Liberty)\raw\ep1\localization\en-us\vo",
]
SEL = r"M:\Projects\Valerie\local-tts\_selected.txt"
OUTROOT = r"M:\Projects\Valerie\local-tts\dataset\valerie_v"
WAVDIR = os.path.join(OUTROOT, "wavs")
META = os.path.join(OUTROOT, "metadata.csv")
LIMIT = int(os.environ.get("BUILD_LIMIT", "0"))

# ffmpeg: normalize loudness, then trim leading/trailing silence (reverse trick), 24k mono 16-bit.
AF = ("loudnorm=I=-18:TP=-1.5:LRA=11,"
      "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.05:detection=peak,"
      "areverse,"
      "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.05:detection=peak,"
      "areverse")


def convert(src, dst):
    r = subprocess.run(
        ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", src,
         "-af", AF, "-ac", "1", "-ar", "24000", "-c:a", "pcm_s16le", dst],
        capture_output=True)
    return r.returncode == 0 and os.path.isfile(dst) and os.path.getsize(dst) > 0


def main():
    os.makedirs(WAVDIR, exist_ok=True)

    selected = [l.strip() for l in open(SEL, encoding="utf-8") if l.strip()]
    if LIMIT:
        selected = selected[:LIMIT]

    # filename -> absolute source path
    index = {}
    for d in RAW_DIRS:
        if os.path.isdir(d):
            for f in os.listdir(d):
                index.setdefault(f, os.path.join(d, f))
    lower = {k.lower(): v for k, v in index.items()}

    # resume set (by wav basename)
    done = set()
    if os.path.isfile(META):
        with open(META, encoding="utf-8") as fh:
            for line in fh:
                p = line.split("|", 1)[0].strip()
                if p and p.lower() != "audio_file":
                    done.add(os.path.basename(p).lower())

    print(f"loading faster-whisper large-v3 (cuda/float16); {len(selected)} clips, {len(done)} already done", flush=True)
    model = WhisperModel("large-v3", device="cuda", compute_type="float16")

    new_file = (not os.path.isfile(META)) or os.path.getsize(META) == 0
    mfh = open(META, "a", encoding="utf-8", newline="")
    if new_file:
        mfh.write("audio_file|text\n")
        mfh.flush()

    t0 = time.time()
    n = skipped = empty = missing = 0
    for i, name in enumerate(selected):
        base = os.path.splitext(name)[0]
        wavname = base + ".wav"
        if wavname.lower() in done:
            skipped += 1
            continue
        src = index.get(name) or lower.get(name.lower())
        if not src:
            missing += 1
            continue
        dst = os.path.join(WAVDIR, wavname)
        if not (os.path.isfile(dst) and os.path.getsize(dst) > 0):
            if not convert(src, dst):
                missing += 1
                continue
        try:
            segs, _ = model.transcribe(dst, language="en", beam_size=5,
                                       condition_on_previous_text=False, vad_filter=False)
            text = " ".join(s.text.strip() for s in segs).strip()
        except Exception as e:  # noqa: BLE001
            print(f"  transcribe error on {wavname}: {e}", flush=True)
            text = ""
        if not text:
            empty += 1
            continue
        text = text.replace("|", " ").replace("\n", " ").strip()
        mfh.write(f"{dst}|{text}\n")
        mfh.flush()
        n += 1
        if n % 100 == 0:
            rate = (time.time() - t0) / n
            eta = rate * (len(selected) - i - 1) / 60
            print(f"[{n}] written | {i + 1}/{len(selected)} scanned | "
                  f"skip {skipped} empty {empty} miss {missing} | {rate:.2f}s/clip | eta {eta:.0f}m", flush=True)

    mfh.close()
    mins = (time.time() - t0) / 60
    print(f"DONE: {n} pairs written | skipped {skipped} | empty {empty} | missing {missing} | {mins:.1f}m", flush=True)


if __name__ == "__main__":
    main()
