# Grave-Lorelai

Grave-Lorelai is a local, audio-native AI companion system built in C# (.NET 8). It features dynamic LLM model selection, an asynchronous spoken audio pipeline, and local visual/ComfyUI integration. 

This repository provides the core product scaffolding. Personal logs, local models, private credentials, and character voice/weight files are kept strictly isolated and ignored.

---

## Features

- **Dynamic Model Selection:** Automatically queries local Ollama tags on boot. Prompts you to choose from matching companion models or default backends, automatically adapting its thinking configuration.
- **Silent Thoughts & Spinner:** Parses internal reasoning traces (via `<think>` blocks or native Ollama thinking fields) silently, rendering a console spinner (`| / - \`) rather than printing thoughts.
- **Dual-Mode TTS Pipeline:**
  - **Cloud Mode:** Streams audio using xAI's "Ara" voice if `GROK_TTS_API_KEY` is configured.
  - **Local Fallback:** Synthesizes audio using a zero-shot local voice-cloned Python server against a reference voice.
- **Spoken-First Queue:** Spoken sentences are queued and played sequentially in a background thread using C# `SoundPlayer`, keeping text streaming and user input completely asynchronous.
- **Visual Integration:** Automatically checks and runs ComfyUI, rendering character selfies based on visual triggers in the LLM's responses.

---

## Setup & Prerequisites

### 1. Requirements
* **.NET 8.0 SDK** (to build and compile the C# codebase)
* **Ollama** installed locally on Windows (defaulting to `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`)
* **Python 3.10+** (if using the local TTS fallback)

---

### 2. Environment Configuration (`.env`)
The application requires a `.env` file at the root directory to load configurations. A template is provided in [example.env](example.env).

Copy the template to create your local `.env`:
```powershell
copy example.env .env
```

#### Configuration Keys:
- `OLLAMA_HOST`: The endpoint where Ollama is running (defaults to `http://localhost:11434`).
- `OLLAMA_MODEL`: Default model to use if no boot menu selection is made (e.g. `revenant/lorelai:latest`).
- `GROK_TTS_API_KEY`: xAI API Key for cloud Ara voice. If left blank or commented out, the application automatically launches and runs the local Python voice-clone fallback.
- `COMFYUI_URL`: The local endpoint for ComfyUI visual generation (defaults to `http://127.0.0.1:8188`).

---

### 3. Git Ignore Setup (`.gitignore`)
To prevent large binary files, custom models, and sensitive personal credentials from entering version control, **you must set up a local `.gitignore`**. 

Ensure your `.gitignore` at the root contains the following baseline:

```gitignore
# User-specific / Local files
.env
*.env
!example.env
Secrets.env
/secrets/
/keys/
*.key
*.pem
config.local.*

# .NET build directories
bin/
obj/
*.user
*.userosscache
*.sln.docstates

# Personal/local runtime files
personal/
data/*
!data/README.md
selfies/*
!selfies/.gitkeep

# Large binary environments and models
img_gen/python_embeded/
models/*.gguf
models/*.bin
models/llm_model.gguf

# ComfyUI dynamic files
img_gen/ComfyUI/input/
img_gen/ComfyUI/output/
img_gen/ComfyUI/models/
img_gen/ComfyUI/temp/
```

> [!IMPORTANT]
> **Git Guardrails:** Never commit API keys, personal credentials, or large model weight files (`.gguf`, `.bin`, `.ckpt`, `.safetensors`) into this repository.

---

## Local Python TTS Setup
If running in **local fallback mode** (no `GROK_TTS_API_KEY` set), the application will start a local HTTP voice-cloning server in the background.

To prepare the local TTS server:
1. Ensure your reference voice WAV file is named `lorelai_combined.wav` and placed in the `/personal/` folder:
   `personal/lorelai_combined.wav`
2. Ensure you have installed your voice synthesis library (e.g., `chatterbox-tts`) in your Python environment.
3. The background process will boot [personal/tts_server.py](personal/tts_server.py) automatically on port `8190`.

---

## How to Run

1. Build the solution to compile the executable:
   ```powershell
   dotnet build Grave-Lorelai.csproj
   ```
2. Run the launcher script from the repository root:
   ```powershell
   .\Launch_Lorelai.bat
   ```
3. Talk to Lorelai. The text will stream in real time, and spoken sentences will play back sequentially.
   - Use `/looks` to view or swap visual checkpoints/LoRAs.
   - Use `/model` in-chat to dynamically hot-swap local Ollama models.
