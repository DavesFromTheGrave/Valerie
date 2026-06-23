using Valerie.Models;

namespace Valerie.Services.Images;

/// <summary>
/// STUB ComfyUI image generator. The real ComfyUI workflow is not designed yet, so this does
/// NOT call ComfyUI. It honours the IImageGenerator contract — accepts a prompt, returns a real
/// file path — by writing a placeholder marker into the output folder. When the workflow JSON is
/// finalized, replace the body of GenerateAsync with the real call; nothing else in the app
/// (the selfie trigger, the console wiring) needs to change.
/// </summary>
public sealed class ComfyUiImageGenerator : IImageGenerator
{
    private readonly ImageOptions _options;

    public ComfyUiImageGenerator(ImageOptions options)
    {
        _options = options;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.OutputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // The real implementation will return a .png produced by ComfyUI. The stub writes a .txt
        // marker so the returned path always points at a file that actually exists.
        var path = Path.Combine(_options.OutputDir, $"valerie_{stamp}_{Slug(prompt)}.placeholder.txt");

        var marker =
            "[Valerie selfie — STUB, ComfyUI not wired yet]\n" +
            $"timestamp : {DateTime.Now:o}\n" +
            $"prompt    : {prompt}\n" +
            $"comfy url : {_options.ComfyUrl}\n" +
            $"workflow  : {_options.WorkflowPath}\n\n" +
            "TODO when the workflow JSON is ready: load the workflow template, inject this prompt\n" +
            "plus a random seed, POST it to {ComfyUrl}/prompt, poll {ComfyUrl}/history/{id} until\n" +
            "it completes, copy the produced image into OutputDir, and return its .png path.\n";

        File.WriteAllText(path, marker);

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"[V would send a selfie here — stub wrote {path}]");
        Console.ResetColor();

        return Task.FromResult(path);
    }

    private static string Slug(string s)
    {
        var clean = new string(s.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (clean.Length > 50) clean = clean[..50];
        return string.IsNullOrEmpty(clean) ? "selfie" : clean;
    }
}
