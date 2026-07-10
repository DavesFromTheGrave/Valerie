using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valerie.Models;

namespace Valerie.Services.Images;

/// <summary>
/// xAI Grok Imagine backend (POST {BaseUrl}/images/generations). Hosted alternative to the local
/// ComfyUI pipeline. Reuses the persona Appearance from ImageGen config so V looks like V, but
/// skips the QualityPrefix (Pony checkpoint score tags — SD jargon that means nothing here) and
/// the negative prompt (the API has no such parameter). Unlike local ComfyUI, xAI moderates
/// output: a filtered result is reported to the console rather than saved.
/// </summary>
public sealed class GrokImageGenerator : IImageGenerator
{
    private readonly GrokImageOptions _options;
    private readonly ImageOptions _shared;   // persona appearance + output dir live here
    private readonly HttpClient _http;

    public GrokImageGenerator(GrokImageOptions options, ImageOptions shared, HttpClient http)
    {
        _options = options;
        _shared = shared;
        _http = http;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    // Nothing to launch — Grok Imagine is a hosted endpoint.
    public Task EnsureComfyRunningAsync() => Task.CompletedTask;

    public async Task<string> GenerateAsync(string sceneDescription, string? filePrefix = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            Console.WriteLine("[Grok Img] No xAI key — can't generate.");
            return "";
        }

        Console.WriteLine($"[Grok Img] Generating... ({_options.Model}, {_options.AspectRatio}, {_options.Resolution})");
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/images/generations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var payload = new
            {
                model = _options.Model,
                prompt = $"{_shared.Appearance}, {sceneDescription}",
                aspect_ratio = _options.AspectRatio,
                resolution = _options.Resolution,
                response_format = "b64_json"
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Grok Img] {(int)response.StatusCode} {response.StatusCode}: {Truncate(raw, 300)}");
                return "";
            }

            var first = JsonNode.Parse(raw)?["data"]?[0];
            if (first is null)
            {
                Console.WriteLine("[Grok Img] Empty response.");
                return "";
            }

            byte[]? bytes = null;
            var b64 = first["b64_json"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(b64))
            {
                bytes = Convert.FromBase64String(b64);
            }
            else
            {
                // URL-only responses use a temporary link — download it immediately.
                var url = first["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(url))
                    bytes = await _http.GetByteArrayAsync(url, ct);
            }

            if (bytes is null || bytes.Length == 0)
            {
                var moderated = first["respect_moderation"]?.GetValue<bool>();
                Console.WriteLine(moderated == false
                    ? "[Grok Img] Image was filtered by xAI moderation."
                    : "[Grok Img] No image data in response.");
                return "";
            }

            var outDir = OutputPaths.EnsureDir(_shared.OutputDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var destName = filePrefix != null
                ? $"{filePrefix}_{timestamp}.jpg"
                : $"valerie_{timestamp}_{OutputPaths.SafeSlug(sceneDescription)}.jpg";
            var destPath = Path.Combine(outDir, destName);

            await File.WriteAllBytesAsync(destPath, bytes, ct);
            Console.WriteLine($"[Valerie sent a photo — saved to: {destName}]");
            Process.Start(new ProcessStartInfo("explorer.exe", outDir) { UseShellExecute = false });
            return destPath;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Grok Img] Failed: {ex.Message}");
            return "";
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
