using System.Collections.Concurrent;
using NAudio.Wave;
using Valerie.Services.Tts;

namespace Valerie.Services.Audio;

/// <summary>
/// Producer/consumer speech playback, ported from Revenant-Echo's streaming model. Sentences are
/// enqueued as the LLM streams; a background worker synthesizes each via <see cref="ITtsClient"/>
/// and plays it, so audio starts after the first sentence instead of the whole reply.
///
/// Playback is fully interruptible: cancelling the token passed to the constructor (barge-in)
/// stops the sentence that's playing right now and discards everything still queued. One instance
/// per turn — dispose it when the turn ends.
/// </summary>
public sealed class SpeechPlayer : IDisposable
{
    private readonly ITtsClient _tts;
    private readonly CancellationToken _ct;
    private readonly BlockingCollection<string> _queue = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private WaveOutEvent? _current;

    public SpeechPlayer(ITtsClient tts, CancellationToken ct)
    {
        _tts = tts;
        _ct = ct;
        _ct.Register(StopNow);            // cancellation == barge-in
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>Whether there's a usable voice at all (no key → text-only, nothing to play).</summary>
    public bool Enabled => _tts.IsConfigured;

    /// <summary>Queue a sentence to be spoken.</summary>
    public void Enqueue(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return;
        try { if (!_queue.IsAddingCompleted) _queue.Add(sentence); }
        catch (InvalidOperationException) { /* completed; ignore */ }
    }

    /// <summary>No more sentences will be queued.</summary>
    public void Complete()
    {
        try { if (!_queue.IsAddingCompleted) _queue.CompleteAdding(); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Completes when everything queued has finished playing (or was cut off).</summary>
    public Task DrainAsync() => _worker;

    /// <summary>Barge-in: stop the sentence playing now and drop the rest of the queue.</summary>
    public void StopNow()
    {
        Complete();
        while (_queue.TryTake(out _)) { }
        lock (_gate) { _current?.Stop(); }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            foreach (var sentence in _queue.GetConsumingEnumerable())
            {
                if (_ct.IsCancellationRequested) break;
                var mp3 = await _tts.SynthesizeAsync(sentence, _ct);
                if (mp3 is null || mp3.Length == 0 || _ct.IsCancellationRequested) continue;
                Play(mp3);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"\n[voice error: {ex.Message}]"); }
    }

    private void Play(byte[] mp3)
    {
        using var ms = new MemoryStream(mp3);
        using var reader = new Mp3FileReader(ms);
        var output = new WaveOutEvent();
        lock (_gate) { _current = output; }
        try
        {
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                if (_ct.IsCancellationRequested) { output.Stop(); break; }
                Thread.Sleep(40);
            }
        }
        finally
        {
            lock (_gate) { _current = null; }
            output.Dispose();
        }
    }

    public void Dispose()
    {
        Complete();
        lock (_gate) { _current?.Stop(); }
        try { _worker.Wait(1000); } catch { /* ignore */ }
        lock (_gate) { _current?.Dispose(); _current = null; }
        _queue.Dispose();
    }
}
