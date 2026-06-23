namespace Valerie.Services.Tts;

public interface ITtsClient
{
    /// <summary>True when a usable API key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Synthesize <paramref name="text"/> in the configured voice and play it on the
    /// default audio device, blocking until playback finishes. Returns true if audio played,
    /// false if it degraded to text-only (no key, API error, or playback failure).</summary>
    Task<bool> SpeakAsync(string text, CancellationToken ct = default);
}
