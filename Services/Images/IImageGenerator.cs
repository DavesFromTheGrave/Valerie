namespace Valerie.Services.Images;

public interface IImageGenerator
{
    Task EnsureComfyRunningAsync();
    Task<string> GenerateAsync(string prompt, string? filePrefix = null, CancellationToken ct = default);
}
