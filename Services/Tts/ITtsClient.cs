namespace Valerie.Services.Tts;

public interface ITtsClient
{
    /// <summary>True when a usable API key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Synthesize <paramref name="text"/> in the configured voice and return the audio
    /// bytes (MP3), or null if not configured, on API error, or if cancelled. Playback is handled
    /// separately by the speech queue so it can be interrupted (barge-in).</summary>
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default);
}
