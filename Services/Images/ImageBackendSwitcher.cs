namespace Valerie.Services.Images;

/// <summary>
/// Routes selfie generation to the active engine: "comfy" (local ComfyUI, the default) or
/// "grok" (hosted xAI Imagine). All photo flows talk to this, so switching engines never
/// touches the call sites. Grok is only offered when an xAI key resolved at startup.
/// </summary>
public sealed class ImageBackendSwitcher : IImageGenerator
{
    private readonly IImageGenerator _comfy;
    private readonly GrokImageGenerator? _grok;

    public ImageBackendSwitcher(IImageGenerator comfy, GrokImageGenerator? grok, string initial)
    {
        _comfy = comfy;
        _grok = grok;
        Active = grok is not null && initial.Equals("grok", StringComparison.OrdinalIgnoreCase)
            ? "grok" : "comfy";
    }

    public string Active { get; private set; }
    public bool GrokAvailable => _grok is not null;
    public IReadOnlyList<string> Names => GrokAvailable ? new[] { "comfy", "grok" } : new[] { "comfy" };

    public bool TrySwitch(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n == "comfy") { Active = "comfy"; return true; }
        if (n == "grok" && _grok is not null) { Active = "grok"; return true; }
        return false;
    }

    /// <summary>Engine for a single shot: the named one if valid, otherwise the active one.</summary>
    public IImageGenerator Pick(string? name = null)
    {
        var n = name?.Trim().ToLowerInvariant();
        if (n == "comfy") return _comfy;
        if (n == "grok" && _grok is not null) return _grok;
        return Active == "grok" && _grok is not null ? _grok : _comfy;
    }

    // ComfyUI startup only matters while it's the active engine.
    public Task EnsureComfyRunningAsync()
        => Active == "comfy" ? _comfy.EnsureComfyRunningAsync() : Task.CompletedTask;

    public Task<string> GenerateAsync(string prompt, string? filePrefix = null, CancellationToken ct = default)
        => Pick().GenerateAsync(prompt, filePrefix, ct);
}
