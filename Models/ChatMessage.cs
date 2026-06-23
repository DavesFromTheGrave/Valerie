namespace Valerie.Models;

/// <summary>A single turn in the conversation. Role is "system", "user", or "assistant".</summary>
public sealed record ChatMessage(string Role, string Content);
