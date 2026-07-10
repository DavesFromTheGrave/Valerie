using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Valerie.Models;

namespace Valerie.Services.Video;

/// <summary>
/// xAI Grok Imagine video (POST {BaseUrl}/videos/generations). The API is asynchronous:
/// submit → request_id → poll /videos/{id} until done/failed/expired → download the mp4
/// promptly (the returned URL is temporary). Text-to-video by default; pass a first-frame
/// image for image-to-video — in that mode the output keeps the image's aspect ratio, so
/// aspect_ratio is only sent for pure text prompts.
/// </summary>
public sealed class GrokVideoGenerator : IVideoGenerator
{
    private readonly VideoOptions _options;
    private readonly HttpClient _http;

    public GrokVideoGenerator(VideoOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<string> GenerateAsync(string prompt, string? firstFramePath = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            Console.WriteLine("[Grok Video] No xAI key — can't generate.");
            return "";
        }

        // --- Submit ---
        string requestId;
        try
        {
            var body = new JsonObject
            {
                ["model"] = _options.Model,
                ["prompt"] = prompt,
                ["duration"] = _options.Duration,
                ["resolution"] = _options.Resolution
            };

            if (firstFramePath is null)
            {
                body["aspect_ratio"] = _options.AspectRatio;
            }
            else
            {
                if (!File.Exists(firstFramePath))
                {
                    Console.WriteLine($"[Grok Video] First-frame image not found: {firstFramePath}");
                    return "";
                }
                var mime = Path.GetExtension(firstFramePath).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                var b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(firstFramePath, ct));
                body["image"] = new JsonObject { ["url"] = $"data:{mime};base64,{b64}" };
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/videos/generations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Grok Video] Submit {(int)response.StatusCode} {response.StatusCode}: {Truncate(raw, 300)}");
                return "";
            }

            requestId = JsonNode.Parse(raw)?["request_id"]?.GetValue<string>() ?? "";
            if (requestId.Length == 0)
            {
                Console.WriteLine("[Grok Video] No request_id in response.");
                return "";
            }
        }
        catch (OperationCanceledException) { return ""; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Grok Video] Submit failed: {ex.Message}");
            return "";
        }

        Console.Write($"[Grok Video] Generating ({_options.Duration}s, {_options.Resolution}) — this takes a few minutes");

        // --- Poll ---
        var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.TimeoutMinutes));
        string? videoUrl = null;
        var finished = false;
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), ct);
                Console.Write(".");

                using var poll = new HttpRequestMessage(
                    HttpMethod.Get, $"{_options.BaseUrl.TrimEnd('/')}/videos/{requestId}");
                poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                using var pollResp = await _http.SendAsync(poll, ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);

                if ((int)pollResp.StatusCode is 401 or 403)
                {
                    Console.WriteLine($"\n[Grok Video] Auth error {(int)pollResp.StatusCode} while polling: {Truncate(pollRaw, 200)}");
                    return "";
                }
                if (!pollResp.IsSuccessStatusCode) continue;   // transient — keep polling

                var node = JsonNode.Parse(pollRaw);
                var status = node?["status"]?.GetValue<string>();
                if (status == "done")
                {
                    finished = true;
                    videoUrl = node?["video"]?["url"]?.GetValue<string>();
                    if (node?["video"]?["respect_moderation"]?.GetValue<bool>() == false)
                        Console.WriteLine("\n[Grok Video] xAI moderation filtered this one.");
                    else
                        Console.WriteLine(" done.");
                    break;
                }
                if (status is "failed" or "expired")
                {
                    var code = node?["error"]?["code"]?.GetValue<string>();
                    var msg = node?["error"]?["message"]?.GetValue<string>();
                    Console.WriteLine($"\n[Grok Video] {status}{(code is null ? "" : $" ({code})")}: {msg ?? "no detail"}");
                    return "";
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            return "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Grok Video] Polling failed: {ex.Message}");
            return "";
        }

        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            Console.WriteLine(finished
                ? "[Grok Video] Finished but returned no video URL."
                : $"\n[Grok Video] Timed out after {_options.TimeoutMinutes} min (request {requestId}).");
            return "";
        }

        // --- Download (the URL is temporary — grab it immediately) ---
        try
        {
            var outDir = OutputPaths.EnsureDir(_options.OutputDir);
            var destName = $"valerie_{DateTime.Now:yyyyMMdd_HHmmss}_{OutputPaths.SafeSlug(prompt)}.mp4";
            var destPath = Path.Combine(outDir, destName);

            var bytes = await _http.GetByteArrayAsync(videoUrl, ct);
            await File.WriteAllBytesAsync(destPath, bytes, ct);
            Console.WriteLine($"[Valerie sent a video — saved to: {destName}]");
            Process.Start(new ProcessStartInfo("explorer.exe", outDir) { UseShellExecute = false });
            return destPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Grok Video] Download failed: {ex.Message}");
            return "";
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
