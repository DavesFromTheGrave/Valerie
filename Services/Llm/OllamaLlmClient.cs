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

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
