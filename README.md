# Valerie (V)

A personal AI companion. Console MVP, .NET 8.

- **Brain** — a Llama-based model served by **Ollama**. The primary endpoint is a remote GPU (Vast.ai) reached over an SSH tunnel; if it's unreachable, V **falls back automatically to a local Ollama** on this machine. Nothing is hardcoded — endpoints come from config.
- **Voice** — **xAI Grok TTS**, the "Ara" voice. The returned MP3 is played automatically after each reply (NAudio). There is no fallback voice by design: if Grok is unavailable, V degrades to text-only for that line. (A future fine-tuned *local* voice clone slots in behind the same `ITtsClient` interface — see project notes.)
- **Selfies** — local **ComfyUI** (stubbed for now). V can share a selfie on her own (`[SEND_PHOTO: ...]` in her reply) or when you ask (`/photo`). The real ComfyUI workflow is a placeholder.

## Configure

Everything lives in `appsettings.json`:

```json
{
  "Llm": { "Endpoints": [ { "Name": "...", "BaseUrl": "...", "Model": "...", "Think": false } ] },
  "Tts": { "BaseUrl": "https://api.x.ai/v1/tts", "ApiKey": "", "Voice": "ara", "Format": "mp3" },
  "ImageGen": { "ComfyUrl": "http://127.0.0.1:8188", "OutputDir": "selfies", "WorkflowPath": "..." }
}
```

**Secrets — the xAI key is stored encrypted at rest (Windows DPAPI), never in plaintext.** One-time setup:

```powershell
dotnet run -- set-key
```

This prompts for the key (masked), encrypts it with DPAPI scoped to **your Windows user**, and writes the ciphertext to `secrets/xai_tts.key` (gitignored). Every launch decrypts it automatically — the plaintext never touches disk and is never committed. The store is per-user/per-machine, so the file is useless if copied elsewhere.

For transient/dev use you can instead set the env var `Tts__ApiKey`, which overrides the store. The xAI key only needs the **TTS / voice** endpoint — chat runs on Ollama, not xAI.

**Endpoints / fallback** — the app tries each endpoint in order and uses the first that answers `/api/tags`. Each endpoint has its own model: your local machine can't hold a 70B, so the local fallback should point at a smaller model (e.g. an 8B).

## Remote GPU tunnel

Tunnel the remote Ollama to a **distinct local port** so it doesn't collide with local Ollama (which owns `11434`):

```powershell
ssh -p <port> root@<host> -L 11500:localhost:11434 -N
```

Then `Llm:Endpoints[0].BaseUrl = http://localhost:11500` (remote 70B) and `[1].BaseUrl = http://localhost:11434` (local fallback). Tunnel up → V uses the remote GPU. Tunnel down → she falls back to local on the next message.

## Run

```powershell
dotnet run
```

Chat by typing. `/photo [description]` forces a selfie. `exit` quits.

## Live voice (talk back)

Full duplex mic ↔ **Ara** via xAI Realtime. Uses `XAI_VOICE_API_KEY` from the DPAPI vault
`A:\env\xai-keys.dpapi` (Realtime-labeled key). Falls back to Valerie full-endpoint key if needed.

```powershell
cd M:\Projects\Valerie
dotnet run -- voice
```

Speak naturally. Server VAD handles turns. **Ctrl+C** hangs up.

Config knobs in `appsettings.json` → `Realtime` (`Model`, `Voice`, `SampleRate`).
Persona comes from `Config/system_prompt.txt` with a short voice-mode prefix.

## System prompt

`Config/system_prompt.txt` — V's persona. Edit it any time; it's re-read on launch, no rebuild needed.
