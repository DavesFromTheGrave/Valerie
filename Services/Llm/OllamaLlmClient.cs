using System.Text;
using System.Text.Json;
using Valerie.Models;

namespace Valerie.Services.Llm;

/// <summary>
/// Talks to an Ollama-compatible /api/chat endpoint. Supports an ordered list of endpoints
/// (e.g. a remote Vast.ai GPU first, then a local Ollama as fallback) and automatically fails
/// over to the next healthy one. If nothing answers, boots local Ollama instead of dying.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly HttpClient _http;

    public LlmEndpoint? ActiveEndpoint { get; private set; }

    public OllamaLlmClient(LlmOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public void SetEndpoint(LlmEndpoint ep) => ActiveEndpoint = ep;

    public async Task<LlmEndpoint?> SelectEndpointAsync(CancellationToken ct = default)
    {
        foreach (var ep in _options.Endpoints)
        {
            if (await IsHealthyAsync(ep, ct))
            {
                ActiveEndpoint = ep;
                return ep;
            }
        }
        ActiveEndpoint = null;
        return null;
    }

    private async Task<bool> IsHealthyAsync(LlmEndpoint ep, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{ep.BaseUrl.TrimEnd('/')}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        await EnsureEndpointAsync(ct);

        var endpoint = ActiveEndpoint!;
        try
        {
            return await StreamOnceAsync(endpoint, messages, onToken, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Mid-session brain died — restart local Ollama once and retry.
            Console.WriteLine($"[brain dropped: {ex.Message}] — restarting Ollama…");
            var localUrl = OllamaBootstrap.IsLocalUrl(endpoint.BaseUrl)
                ? endpoint.BaseUrl
                : OllamaBootstrap.DefaultBaseUrl;
            await OllamaBootstrap.EnsureRunningAsync(localUrl, TimeSpan.FromSeconds(45),
                s => Console.WriteLine($"  {s}"));
            await EnsureEndpointAsync(ct);
            return await StreamOnceAsync(ActiveEndpoint!, messages, onToken, ct);
        }
    }

    private async Task EnsureEndpointAsync(CancellationToken ct)
    {
        if (ActiveEndpoint is not null && await IsHealthyAsync(ActiveEndpoint, ct))
            return;

        var ep = await SelectEndpointAsync(ct);
        if (ep is null)
        {
            var localUrl = _options.Endpoints
                .Select(e => e.BaseUrl)
                .FirstOrDefault(OllamaBootstrap.IsLocalUrl) ?? OllamaBootstrap.DefaultBaseUrl;

            Console.WriteLine("[brain cold — starting local Ollama…]");
            await OllamaBootstrap.EnsureRunningAsync(localUrl, TimeSpan.FromSeconds(60),
                s => Console.WriteLine($"  {s}"));
            ep = await SelectEndpointAsync(ct);
        }

        if (ep is null)
            throw new InvalidOperationException(
                "No LLM endpoint is reachable (and local Ollama didn't come up). Checked: " +
                string.Join(", ", _options.Endpoints.Select(e => $"{e.Name} @ {e.BaseUrl}")) + ".");
    }

    private async Task<string> StreamOnceAsync(
        LlmEndpoint endpoint,
        IReadOnlyList<ChatMessage> messages,
        Action<string> onToken,
        CancellationToken ct)
    {
        var payload = new
        {
            model = endpoint.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
            think = endpoint.Think
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 200)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement chunk;
                try { chunk = JsonSerializer.Deserialize<JsonElement>(line); }
                catch { continue; }

                if (chunk.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentEl))
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        full.Append(text);
                        onToken(text);
                    }
                }

                if (chunk.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Barge-in: keep whatever V said so far.
        }

        return full.ToString();
    }

    /// <summary>
    /// Unload the local model from VRAM (Ollama keep_alive:0) and wait until it's actually gone,
    /// so a local ComfyUI selfie has room on an 8GB card. Remote endpoints are left alone — their
    /// GPU doesn't share this machine's memory. The model reloads on the next chat turn (~seconds).
    /// </summary>
    public async Task ReleaseVramAsync(CancellationToken ct = default)
    {
        var ep = ActiveEndpoint;
        if (ep is null || !OllamaBootstrap.IsLocalUrl(ep.BaseUrl))
            return; // remote GPU (or no endpoint) — nothing to free on this machine.

        var baseUrl = ep.BaseUrl.TrimEnd('/');
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[freeing the GPU for a photo — brain unloads, reloads after]");
            Console.ResetColor();

            // keep_alive:0 tells Ollama to evict this model from VRAM right after the call.
            var payload = new { model = ep.Model, keep_alive = 0 };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/generate")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);

            // Confirm it actually left VRAM before we let the image pipeline grab the card.
            for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    using var ps = await _http.GetAsync($"{baseUrl}/api/ps", ct);
                    var body = await ps.Content.ReadAsStringAsync(ct);
                    var doc = JsonSerializer.Deserialize<JsonElement>(body);
                    var stillResident = doc.TryGetProperty("models", out var models)
                        && models.ValueKind == JsonValueKind.Array
                        && models.EnumerateArray().Any(m =>
                            m.TryGetProperty("name", out var nm) && nm.GetString() == ep.Model);
                    if (!stillResident) return; // freed
                }
                catch { /* transient — keep polling */ }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[vram] couldn't unload the brain (continuing anyway): {ex.Message}");
        }
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
