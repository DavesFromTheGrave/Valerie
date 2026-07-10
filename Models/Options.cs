namespace Valerie.Models;

/// <summary>Root of appsettings.json, bound via IConfiguration.</summary>
public sealed class AppOptions
{
    public LlmOptions Llm { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();
    public LocalTtsOptions LocalTts { get; set; } = new();
    public ImageOptions ImageGen { get; set; } = new();
    public GrokImageOptions GrokImage { get; set; } = new();
    public VideoOptions VideoGen { get; set; } = new();
}

public sealed class LlmOptions
{
    /// <summary>Tried in order; the first one that responds is used. Put the remote GPU first,
    /// local Ollama last, so V falls back automatically when the rental GPU is gone.</summary>
    public List<LlmEndpoint> Endpoints { get; set; } = new();
}

public sealed class LlmEndpoint
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Ask Ollama to emit a thinking trace. Kept off by default for the spoken path.</summary>
    public bool Think { get; set; }
}

public sealed class TtsOptions
{
    public string BaseUrl { get; set; } = "https://api.x.ai/v1/tts";
    public string ApiKey { get; set; } = "";
    public string Voice { get; set; } = "ara";
    public string Format { get; set; } = "mp3";
    public string Language { get; set; } = "en";
}

/// <summary>Local F5-TTS voice served by the Python server in local-tts/. When Prefer is true and
/// the server answers /health, V uses this instead of Grok — offline, cloned voice. Otherwise the
/// app behaves exactly as before (Grok, or text-only).</summary>
public sealed class LocalTtsOptions
{
    /// <summary>Use the local voice when its server is reachable. Off = current Grok/text behavior.</summary>
    public bool Prefer { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:8123";
}

public sealed class ImageOptions
{
    /// <summary>Image engine active at startup: "comfy" (local ComfyUI) or "grok" (xAI Imagine).</summary>
    public string Backend { get; set; } = "comfy";
    public string ComfyUrl { get; set; } = "http://127.0.0.1:8188";
    public string OutputDir { get; set; } = "selfies";
    public string WorkflowPath { get; set; } = "workflows/valerie_selfie.json";
    public string LaunchBat { get; set; } = "";
    public string Checkpoint { get; set; } = "cyberrealisticPony_v180Coreshift.safetensors";
    public string QualityPrefix { get; set; } = "score_9, score_8_up, score_7_up, source_photo, ";
    public string Appearance { get; set; } =
        "slender 5'1\" nubile young woman, vibrant fiery red hair in big loose waves, " +
        "striking bright blue eyes with gold flecks and dark blue ring, " +
        "pale ivory skin with light red and brown freckles on nose cheeks shoulders and chest, " +
        "oval face with small pointed chin, full light pink lips, " +
        "high detail skin texture, cinematic lighting, photorealistic";
    public string NegativePrompt { get; set; } =
        "worst quality, low quality, blurry, deformed, ugly, bad anatomy, extra limbs, " +
        "watermark, text, signature, duplicate, mutation, out of frame";
}

/// <summary>xAI Grok Imagine image generation — hosted alternative to local ComfyUI.</summary>
public sealed class GrokImageOptions
{
    public string BaseUrl { get; set; } = "https://api.x.ai/v1";
    /// <summary>Resolved from A:\env\xai-keys.dpapi at startup (Valerie key first); config/env override wins.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "grok-imagine-image-quality";
    /// <summary>2:3 portrait matches the ComfyUI selfie framing (512×768).</summary>
    public string AspectRatio { get; set; } = "2:3";
    /// <summary>"1k" or "2k".</summary>
    public string Resolution { get; set; } = "1k";
}

/// <summary>xAI Grok Imagine video generation (text-to-video and image-to-video).</summary>
public sealed class VideoOptions
{
    public string BaseUrl { get; set; } = "https://api.x.ai/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "grok-imagine-video";
    public string OutputDir { get; set; } = "videos";
    /// <summary>Seconds, 1–15.</summary>
    public int Duration { get; set; } = 8;
    /// <summary>Used for text-to-video only; image-to-video keeps the source image's ratio.</summary>
    public string AspectRatio { get; set; } = "16:9";
    /// <summary>"480p" or "720p" ("1080p" only on grok-imagine-video-1.5 image-to-video).</summary>
    public string Resolution { get; set; } = "720p";
    public int PollSeconds { get; set; } = 5;
    public int TimeoutMinutes { get; set; } = 10;
}
