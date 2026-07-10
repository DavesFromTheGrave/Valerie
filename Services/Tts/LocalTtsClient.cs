using System.Text;
using System.Text.Json;
using Valerie.Models;

namespace Valerie.Services.Tts;

/// <summary>
/// Local F5-TTS voice via the Python server in local-tts/ (POST /tts -> MP3). This is the
/// offline, owns-the-voice path: V speaks in the cloned voice with no cloud dependency. Same
/// contract as <see cref="GrokTtsClient"/> (returns MP3 bytes for the NAudio playback path), so
/// it's a drop-in — selection happens in Program.cs.
///
/// Availability is probed against /health at startup rather than assumed: the server is a
/// separate process that may not be running, in which case V falls back to Grok/text per config.
/// </summary>
public sealed class LocalTtsClient : ITtsClient
{
    private readonly LocalTtsOptions _options;
    private readonly HttpClient _http;
    private bool _healthy;

    public LocalTtsClient(LocalTtsOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public bool IsConfigured => _healthy;

    /// <summary>Ping /health so we only advertise the local voice when the server is actually up.
    /// Returns true if the F5-TTS server answered.</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_options.BaseUrl.TrimEnd('/')}/health", ct);
            if (!resp.IsSuccessStatusCode) { _healthy = false; return false; }
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            _healthy = status is "ok" or "loading";
            return _healthy;
        }
        catch
        {
            _healthy = false;
            return false;
        }
    }

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_healthy) return null;

        try
        {
            var payload = JsonSerializer.Serialize(new { text, format = "mp3" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/tts", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"\n[local voice: F5-TTS {(int)response.StatusCode} — {Truncate(err, 200)}]");
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
            Console.WriteLine($"\n[local voice unavailable: {ex.Message}]");
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
