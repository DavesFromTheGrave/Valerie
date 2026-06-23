using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Valerie.Models;

namespace Valerie.Services.Tts;

/// <summary>
/// xAI Grok TTS using the "Ara" voice. Synthesizes MP3 audio; playback (and interruption) is
/// owned by the speech queue, not this client. There is intentionally no local/open-source TTS
/// fallback — if Grok is unavailable, V degrades to text-only rather than substituting a
/// worse-sounding voice. (A future fine-tuned LOCAL clone arrives as a separate ITtsClient.)
/// The endpoint, voice, format and language all come from config so the request can be tuned to
/// xAI's live TTS API without recompiling.
/// </summary>
public sealed class GrokTtsClient : ITtsClient
{
    private readonly TtsOptions _options;
    private readonly HttpClient _http;

    public GrokTtsClient(TtsOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsConfigured) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var payload = new
            {
                text,
                voice_id = _options.Voice,
                language = _options.Language,
                output_format = new { codec = _options.Format }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"\n[voice unavailable: xAI TTS {(int)response.StatusCode} {response.StatusCode} — {Truncate(body, 200)}]");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException)
        {
            return null; // barge-in / shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[voice unavailable: {ex.Message}]");
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
