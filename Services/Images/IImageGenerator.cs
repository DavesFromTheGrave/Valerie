namespace Valerie.Services.Images;

public interface IImageGenerator
{
    /// <summary>Generate an image for <paramref name="prompt"/> and return the path to the saved file.</summary>
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}
