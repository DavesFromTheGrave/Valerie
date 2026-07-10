namespace Valerie.Services.Video;

public interface IVideoGenerator
{
    bool IsConfigured { get; }

    /// <summary>Text-to-video, or image-to-video when a first-frame image path is given.
    /// Returns the saved .mp4 path, or "" on failure (errors are logged, never thrown).</summary>
    Task<string> GenerateAsync(string prompt, string? firstFramePath = null, CancellationToken ct = default);
}
