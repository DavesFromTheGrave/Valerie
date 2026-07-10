using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Valerie.Services.Voice;

/// <summary>
/// xAI Voice Agent (Realtime) duplex session: mic → wss://api.x.ai/v1/realtime → Ara → speakers.
/// OpenAI-Realtime-compatible event names where possible.
/// </summary>
public sealed class RealtimeVoiceSession : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly int _sampleRate;
    private readonly Action<string> _log;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playBuffer;
    private Task? _receiveLoop;
    private int _appendedBytes;

    public RealtimeVoiceSession(
        string apiKey,
        string instructions,
        string model = "grok-voice-latest",
        string voice = "ara",
        int sampleRate = 24000,
        Action<string>? log = null)
    {
        _apiKey = apiKey;
        _instructions = instructions;
        _model = model;
        _voice = voice;
        _sampleRate = sampleRate;
        _log = log ?? (s => Console.WriteLine(s));
    }

    public async Task RunAsync(CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        var uri = new Uri($"wss://api.x.ai/v1/realtime?model={Uri.EscapeDataString(_model)}");
        _log($"Connecting {_model} / voice={_voice} …");
        await _ws.ConnectAsync(uri, ct);
        _log("Connected. Configuring session…");

        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                voice = _voice,
                instructions = _instructions,
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.85,
                    silence_duration_ms = 700,
                    prefix_padding_ms = 300
                },
                audio = new
                {
                    input = new { format = new { type = "audio/pcm", rate = _sampleRate } },
                    output = new { format = new { type = "audio/pcm", rate = _sampleRate } }
                }
            }
        }, ct);

        StartPlayback();
        StartMic(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(ct), ct);

        _log("Listening. Talk naturally — Ctrl+C to hang up.");
        _log("(Server VAD handles turn-taking. Speak, pause, she answers.)");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // normal hangup
        }
    }

    private void StartPlayback()
    {
        _playBuffer = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_playBuffer);
        _waveOut.Play();
    }

    private void StartMic(CancellationToken ct)
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_sampleRate, 16, 1),
            BufferMilliseconds = 40
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (ct.IsCancellationRequested || _ws?.State != WebSocketState.Open) return;
            if (e.BytesRecorded <= 0) return;

            // Drop near-silence to save bandwidth (simple RMS gate)
            if (IsMostlySilence(e.Buffer, e.BytesRecorded)) return;

            var slice = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, slice, 0, e.BytesRecorded);
            var b64 = Convert.ToBase64String(slice);
            _ = SendJsonAsync(new
            {
                type = "input_audio_buffer.append",
                audio = b64
            }, ct);

            Interlocked.Add(ref _appendedBytes, e.BytesRecorded);
        };

        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                _log($"[mic error] {e.Exception.Message}");
        };

        _waveIn.StartRecording();
        _log($"Mic open @ {_sampleRate} Hz mono PCM16.");
    }

    private static bool IsMostlySilence(byte[] buffer, int count)
    {
        long sum = 0;
        var samples = count / 2;
        if (samples == 0) return true;
        for (var i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += Math.Abs(s);
        }
        var avg = sum / samples;
        return avg < 180; // low threshold — still send quiet speech
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[1024 * 64];
        var msg = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                msg.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log("Server closed the socket.");
                        _cts?.Cancel();
                        return;
                    }
                    msg.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                HandleServerEvent(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[receive] {ex.Message}");
            _cts?.Cancel();
        }
    }

    private void HandleServerEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString() ?? "";

            switch (type)
            {
                case "session.updated":
                    _log("Session ready. Go ahead.");
                    break;

                case "session.created":
                case "conversation.created":
                    break;

                case "input_audio_buffer.speech_started":
                    // Barge-in: clear local playback so she stops talking under you
                    _playBuffer?.ClearBuffer();
                    Console.Write("\r[you] …          ");
                    break;

                case "input_audio_buffer.speech_stopped":
                    Console.Write("\r[you] (processing)");
                    break;

                case "conversation.item.input_audio_transcription.completed":
                case "conversation.item.input_audio_transcription.updated":
                    if (root.TryGetProperty("transcript", out var tr))
                        Console.WriteLine($"\rYou: {tr.GetString()}");
                    break;

                case "response.output_audio_transcript.delta":
                    if (root.TryGetProperty("delta", out var d))
                        Console.Write(d.GetString());
                    break;

                case "response.output_audio_transcript.done":
                    Console.WriteLine();
                    break;

                case "response.audio.delta":
                case "response.output_audio.delta":
                    // base64 PCM16 chunk
                    string? b64 = null;
                    if (root.TryGetProperty("delta", out var delta))
                        b64 = delta.GetString();
                    else if (root.TryGetProperty("audio", out var audio))
                        b64 = audio.GetString();
                    if (!string.IsNullOrEmpty(b64) && _playBuffer != null)
                    {
                        var pcm = Convert.FromBase64String(b64);
                        _playBuffer.AddSamples(pcm, 0, pcm.Length);
                    }
                    break;

                case "response.done":
                    Console.WriteLine();
                    break;

                case "error":
                    var msg = root.TryGetProperty("error", out var err)
                        ? err.ToString()
                        : root.TryGetProperty("message", out var m) ? m.GetString() : json;
                    _log($"[server error] {msg}");
                    break;

                default:
                    // Uncomment for protocol debugging:
                    // _log($"[event] {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"[parse] {ex.Message}");
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _waveIn?.StopRecording(); } catch { /* ignore */ }
        _waveIn?.Dispose();
        _waveIn = null;

        try { _waveOut?.Stop(); } catch { /* ignore */ }
        _waveOut?.Dispose();
        _waveOut = null;
        _playBuffer = null;

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
            _ws.Dispose();
            _ws = null;
        }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { /* ignore */ }
        }

        _cts?.Dispose();
    }
}
