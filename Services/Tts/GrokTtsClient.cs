using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Valerie.Models;

namespace Valerie.Services.Tts;

/// <summary>
/// xAI Grok TTS using the "Ara" voice. Requests MP3 audio and plays it with NAudio.
/// There is intentionally no local/open-source TTS fallback here — if Grok is unavailable, V
/// degrades to text-only for that line rather than substituting a worse-sounding voice. (A
/// future fine-tuned LOCAL voice clone would arrive as a SEPARATE ITtsClient implementation,
/// mirroring the LLM's remote-to-local fallback — not as a degraded fallback inside this one.)
/// The endpoint, voice, format and language all come from config so the request can be tuned
/// to xAI's live TTS API without recompiling.
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

    public async Task<bool> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!IsConfigured)
        {
            Console.WriteLine("[voice unavailable: no xAI API key configured]");
            return false;
        }

        byte[] audio;
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
                Console.WriteLine($"[voice unavailable: xAI TTS {(int)response.StatusCode} {response.StatusCode} — {Truncate(body, 200)}]");
                return false;
            }

            audio = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[voice unavailable: {ex.Message}]");
            return false;
        }

        if (audio.Length == 0)
        {
            Console.WriteLine("[voice unavailable: empty audio response]");
            return false;
        }

        try
        {
            await PlayMp3Async(audio, ct);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[voice playback error: {ex.Message}]");
            return false;
        }
    }

    private static async Task PlayMp3Async(byte[] mp3, CancellationToken ct)
    {
        using var ms = new MemoryStream(mp3);
        using var reader = new Mp3FileReader(ms);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            if (ct.IsCancellationRequested)
            {
                output.Stop();
                break;
            }
            await Task.Delay(80, ct);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
