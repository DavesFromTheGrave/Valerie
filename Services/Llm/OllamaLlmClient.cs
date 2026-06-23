using System.Text;
using System.Text.Json;
using Valerie.Models;

namespace Valerie.Services.Llm;

/// <summary>
/// Talks to an Ollama-compatible /api/chat endpoint. Supports an ordered list of endpoints
/// (e.g. a remote Vast.ai GPU first, then a local Ollama as fallback) and automatically fails
/// over to the next healthy one. Nothing about the endpoint is hardcoded — it all comes from config.
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

    public async Task<string> StreamChatAsync(IReadOnlyList<ChatMessage> messages, Action<string> onToken, CancellationToken ct = default)
    {
        // Ensure a live endpoint; re-probe (failover) if the active one has gone away.
        if (ActiveEndpoint is null || !await IsHealthyAsync(ActiveEndpoint, ct))
        {
            var ep = await SelectEndpointAsync(ct);
            if (ep is null)
                throw new InvalidOperationException(
                    "No LLM endpoint is reachable. Checked: " +
                    string.Join(", ", _options.Endpoints.Select(e => $"{e.Name} @ {e.BaseUrl}")) + ".");
        }

        var endpoint = ActiveEndpoint!;
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
        response.EnsureSuccessStatusCode();

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
            // Barge-in: stop streaming and keep whatever V has said so far.
        }

        return full.ToString();
    }
}
