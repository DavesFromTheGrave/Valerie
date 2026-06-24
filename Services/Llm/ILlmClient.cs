using Valerie.Models;

namespace Valerie.Services.Llm;

public interface ILlmClient
{
    /// <summary>The endpoint currently in use, or null until one has been selected.</summary>
    LlmEndpoint? ActiveEndpoint { get; }

    /// <summary>Probe the configured endpoints in order and select the first that responds.</summary>
    Task<LlmEndpoint?> SelectEndpointAsync(CancellationToken ct = default);

    /// <summary>Directly set the active endpoint without probing (for manual /model switching).</summary>
    void SetEndpoint(LlmEndpoint ep);

    /// <summary>Stream a chat completion. Each content token is handed to <paramref name="onToken"/>
    /// as it arrives; the full assembled assistant text is returned.</summary>
    Task<string> StreamChatAsync(IReadOnlyList<ChatMessage> messages, Action<string> onToken, CancellationToken ct = default);
}
