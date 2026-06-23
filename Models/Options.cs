namespace Valerie.Models;

/// <summary>Root of appsettings.json, bound via IConfiguration.</summary>
public sealed class AppOptions
{
    public LlmOptions Llm { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();
    public ImageOptions ImageGen { get; set; } = new();
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

public sealed class ImageOptions
{
    public string ComfyUrl { get; set; } = "http://127.0.0.1:8188";
    public string OutputDir { get; set; } = "selfies";
    public string WorkflowPath { get; set; } = "workflows/valerie_selfie.placeholder.json";
}
