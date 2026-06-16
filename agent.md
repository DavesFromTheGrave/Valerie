# agent.md - Grave-Lorelai (Lorelai Product Build)

Living record of architecture, progress, decisions, and next steps for the **Lorelai product only**. Update this file instead of creating separate tickets when work is complete.

## Project Overview

Grave-Lorelai is the proprietary Revenant Systems build for **Lorelai** — a voice-first conversational companion (speakers in, speakers out; text for accessibility and debugging only).

- Double-click EXE vision: boot Ollama, loading sequence, first-boot name collection, immersive audio conversation.
- Self-contained in the Grave-Lorelai folder.
- `personal/` holds local-only runtime data (never committed).
- Persona source: `docs/Lorelai_Prompt.txt` baked into a local Ollama modelfile (private, not in this repo).

Current branch (`grave-lorelai/text-gen-base`): text foundation + audio pipeline scaffold.

## Current Architecture (Text-Gen Base)

- **Language/Runtime**: C# .NET 8 (`Grave-Lorelai.csproj`, `Program.cs`).
- **LLM Backend**: Ollama (local only). Default model tag configurable via `.env` / boot menu.
- **Conversation**: Full in-memory history; streaming `/api/chat`; thinking stripped app-side (spinner + `t` / `thoughts`).
- **TTS**: Dual-mode — xAI Ara (if `GROK_TTS_API_KEY`) or local Python server on `:8190`.
- **Visuals**: ComfyUI integration, `[SEND_PHOTO:…]` tags, `/looks` commands.
- **Voice pipeline patterns**: Reference sibling **Revenant-Echo** (`src/backend.py`, `main.py`) for turn-taking and streaming — do not commit Echo internals here.

## Data Isolation

- `personal/` — local credentials, voice refs, chats, modelfiles (gitignored).
- `data/` — runtime user name, session paths documented in `data/README.md`.
- `example.env` — safe template only; real `.env` stays local.

## Coding Musts

1. Robust — fallbacks for missing audio, Ollama down, parse failures.
2. Simplicity — minimal deps; lean streaming path for low latency.
3. Quickness — spoken text streams before full turn completes.
4. Secure — nothing sensitive committed; local-only by design.

## How to Build / Run

```powershell
cd M:\Projects\Grave-Lorelai
dotnet run
# or Launch_Lorelai.bat after build
```

- First run: loading → optional first-boot WAV → name prompt → conversation.
- `t` / `thoughts` after a turn for internal reasoning.
- `exit` to quit.

## Next Steps

- Restart audio layer (local TTS shootout; optional BYOK cloud voice).
- Full voice I/O: Windows STT, Echo-style follow-up windows.
- Voice-driven first-boot name after greeting WAV.
- GPU handoff: Ollama ↔ TTS ↔ ComfyUI on 3070 Ti.
- Product polish: double-click EXE lifecycle for all background services.

## Incident Log

- **Past session:** Accidental deletion of legacy Lorelai folder — established rule: *destructive actions require explicit approval.*
- **June 14, 2026:** Unapproved branch deletions / force-push — *no implied permission.*
- **June 16, 2026:** Public repo exposed proprietary doc leakage — repo moved private; product tree scrubbed.

Last updated: June 16, 2026.
