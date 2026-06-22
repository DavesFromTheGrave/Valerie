# Valerie

Valerie is a local, audio-native AI companion system built in C# (.NET 8). It features dynamic LLM model selection, an asynchronous spoken audio pipeline, and local visual/ComfyUI integration. 

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
- `OLLAMA_MODEL`: Default model to use if no boot menu selection is made (e.g. `revenant/nsfw-v-8b:latest`).
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
1. Ensure your reference voice WAV file is named `v_combined.wav` and placed in the `/personal/` folder:
   `personal/v_combined.wav`
2. Ensure you have installed your voice synthesis library (e.g., `chatterbox-tts`) in your Python environment.
3. The background process will boot [personal/tts_server.py](personal/tts_server.py) automatically on port `8190`.

---

## Remote Setup (Vast.ai — both LLM and TTS on the server)

Both Ollama and StyleTTS2 run on the remote GPU instance. Yggdrasil connects via SSH tunnels and treats both as localhost services — no `.env` changes needed.

### 1. Upload and run the setup script

```powershell
scp -P 20931 tts/setup_remote_tts.sh root@175.155.64.149:/workspace/
ssh -p 20931 root@175.155.64.149 "bash /workspace/setup_remote_tts.sh"
```

Installs Ollama, clones StyleTTS2, downloads the LibriTTS multispeaker checkpoint, and starts both services.

### 2. Upload the Ollama modelfile and create the model

```powershell
scp -P 20931 personal/modelfiles/nsfw-v-9b.modelfile root@175.155.64.149:/workspace/
ssh -p 20931 root@175.155.64.149 "ollama create revenant/nsfw-v-9b:latest -f /workspace/nsfw-v-9b.modelfile"
```

The base model weights will be pulled automatically during `ollama create`.

### 3. Upload voice reference clips

```powershell
scp -P 20931 -r tts\voice_data\cp2077_femV\* root@175.155.64.149:/workspace/voice_data/
ssh -p 20931 root@175.155.64.149 "bash /workspace/restart_tts.sh"
```

`restart_tts.sh` is written by the setup script. It kills the running TTS server and relaunches it so the voice embedding is computed from the uploaded clips.

### 4. Open SSH tunnels (Yggdrasil)

```powershell
ssh -p 20931 root@175.155.64.149 -L 11434:localhost:11434 -L 8190:localhost:8190 -N
```

| Port | Service |
|---|---|
| `11434` | Ollama LLM API |
| `8190` | StyleTTS2 voice server |

---

## How to Run

1. Build the solution to compile the executable:
   ```powershell
   dotnet build Valerie.csproj
   ```
2. Run the launcher script from the repository root:
   ```powershell
   .\Launch_Valerie.bat
   ```
3. Talk to Valerie. The text will stream in real time, and spoken sentences will play back sequentially.
   - Use `/looks` to view or swap visual checkpoints/LoRAs.
   - Use `/model` in-chat to dynamically hot-swap local Ollama models.
