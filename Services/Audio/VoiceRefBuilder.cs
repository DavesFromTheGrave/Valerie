using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Valerie.Security;

namespace Valerie.Services.Audio;

/// <summary>
/// Builds a single ≤120s reference WAV for xAI custom-voice cloning out of a folder of short
/// voice clips. Scans every clip (duration, loudness, clipping, silence), scores them, then
/// stitches the best ones — loudness-normalized, 250ms gaps — into a 24kHz/16-bit/mono WAV
/// ready for the xAI console. Also writes a per-clip scan CSV, which doubles as the dataset
/// inventory for the future local fine-tune track.
///
/// Usage: dotnet run -- voice-ref &lt;clipFolder&gt; [--out file.wav] [--target seconds] [--list picks.txt] [--scan-only]
///   --out       output path (default: voice_ref.wav in the current directory)
///   --target    seconds of speech to aim for (default 110; hard cap 118)
///   --list      skip scoring — stitch exactly these clips, in order. Accepts plain filenames
///               (one per line) or ffmpeg concat syntax (file 'name.wav'). Paths are relative
///               to the clip folder unless absolute.
///   --scan-only inventory + CSV only; don't build a reference (fine-tune dataset survey)
///
/// Custom-voice management (terminal replacement for the console.x.ai UI):
///   voice-ref clone [ref.wav] [--name Valerie]   upload the reference → create the voice, print voice_id
///   voice-ref voices                             list the team's custom voices
///   voice-ref delete &lt;voice_id&gt;                  remove a custom voice
/// </summary>
public static class VoiceRefBuilder
{
    private const int TargetRate = 24000;
    private const double HardCapSeconds = 118;   // stay safely under the 120s API limit
    private const double GapSeconds = 0.25;
    private const double TargetRmsDb = -18;
    private const double PeakCeiling = 0.891;    // -1 dBFS
    private const double MaxClipSeconds = 30;    // anything longer is a compilation, not a line

    private sealed record ClipInfo(
        string Path, double Seconds, int SampleRate, int Channels,
        double RmsDb, double PeakDb, double Peak, double ClipFrac, double SilenceFrac, double Score);

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--")) return Usage();

        // API subcommands — full custom-voice lifecycle without touching the console UI.
        switch (args[0].ToLowerInvariant())
        {
            case "clone":  return CloneAsync(args[1..]).GetAwaiter().GetResult();
            case "voices": return ListVoicesAsync().GetAwaiter().GetResult();
            case "delete": return args.Length > 1 ? DeleteVoiceAsync(args[1]).GetAwaiter().GetResult() : Usage();
        }

        var folder = args[0];
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return 1;
        }

        var outPath = "voice_ref.wav";
        double targetSeconds = 110;
        string? listPath = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--out") outPath = args[i + 1];
            if (args[i] == "--target" && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) targetSeconds = t;
            if (args[i] == "--list") listPath = args[i + 1];
        }
        var scanOnly = args.Contains("--scan-only");
        targetSeconds = Math.Min(targetSeconds, HardCapSeconds);

        List<ClipInfo> selection;
        if (listPath is null)
        {
            var clips = ScanFolder(folder);
            if (clips.Count == 0)
            {
                Console.WriteLine("No usable clips found.");
                return 1;
            }
            WriteScanCsv(clips, Path.ChangeExtension(outPath, null) + "_scan.csv");
            if (scanOnly) return 0;
            selection = SelectBest(clips, targetSeconds);
        }
        else
        {
            selection = ScanPickList(folder, listPath);
        }

        if (selection.Count == 0)
        {
            Console.WriteLine("Nothing selected — no clips scored above zero (or the pick list was empty).");
            return 1;
        }

        var total = selection.Sum(c => c.Seconds) + GapSeconds * (selection.Count - 1);
        Console.WriteLine($"Selected {selection.Count} clips, {total:F1}s of reference audio.");

        Assemble(selection, outPath);
        WriteManifest(selection, outPath, total);

        Console.WriteLine();
        Console.WriteLine($"Reference written: {Path.GetFullPath(outPath)}");
        Console.WriteLine("Next: upload it at console.x.ai → Voice → Voice Library → clone,");
        Console.WriteLine("copy the voice_id, and put it in appsettings.json (Tts:Voice and Realtime:Voice).");
        Console.WriteLine("If the delivery feels off, audition the manifest and re-run with --list of hand-picked clips.");
        return 0;
    }

    // ---- Scan ----

    private static List<ClipInfo> ScanFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "*.mp3", SearchOption.AllDirectories))
            .OrderBy(f => f).ToList();
        Console.WriteLine($"Scanning {files.Count} audio files in {folder} ...");

        var clips = new List<ClipInfo>();
        int done = 0, skippedLong = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                var info = Analyze(file, out var tooLong);
                if (tooLong) { skippedLong++; }
                else if (info is not null) clips.Add(info);
            }
            catch { failed++; }

            if (++done % 200 == 0) Console.WriteLine($"  {done}/{files.Count}");
        }

        var totalH = clips.Sum(c => c.Seconds) / 3600;
        Console.WriteLine($"Scanned {clips.Count} usable clips ({totalH:F2} h of audio)" +
                          $"{(skippedLong > 0 ? $", skipped {skippedLong} over {MaxClipSeconds}s (compilations)" : "")}" +
                          $"{(failed > 0 ? $", {failed} unreadable" : "")}.");

        var rates = clips.GroupBy(c => $"{c.SampleRate}Hz/{c.Channels}ch").Select(g => $"{g.Key}×{g.Count()}");
        Console.WriteLine($"Formats: {string.Join(", ", rates)}. Mean loudness {clips.Average(c => c.RmsDb):F1} dBFS RMS.");
        return clips;
    }

    private static ClipInfo? Analyze(string file, out bool tooLong)
    {
        tooLong = false;
        using var reader = new AudioFileReader(file);
        var seconds = reader.TotalTime.TotalSeconds;
        if (seconds > MaxClipSeconds) { tooLong = true; return null; }
        if (seconds < 0.3) return null;

        double sumSq = 0, peak = 0;
        long n = 0, clipped = 0, silent = 0;
        var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                var a = Math.Abs(buf[i]);
                sumSq += (double)buf[i] * buf[i];
                if (a > peak) peak = a;
                if (a >= 0.999) clipped++;
                if (a < 0.003) silent++;   // ≈ -50 dBFS
                n++;
            }
        }
        if (n == 0) return null;

        var rms = Math.Sqrt(sumSq / n);
        var rmsDb = 20 * Math.Log10(Math.Max(rms, 1e-9));
        var peakDb = 20 * Math.Log10(Math.Max(peak, 1e-9));
        var clipFrac = (double)clipped / n;
        var silenceFrac = (double)silent / n;

        return new ClipInfo(file, seconds, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels,
            rmsDb, peakDb, peak, clipFrac, silenceFrac,
            ScoreClip(seconds, rmsDb, clipFrac, silenceFrac));
    }

    /// <summary>Higher is better. Favors complete conversational lines (4–10s), healthy
    /// loudness, no clipping, and not-mostly-silence.</summary>
    private static double ScoreClip(double seconds, double rmsDb, double clipFrac, double silenceFrac)
    {
        double dur = seconds switch
        {
            < 2 or > 15 => 0,
            < 4 => (seconds - 2) / 2,
            > 10 => (15 - seconds) / 5,
            _ => 1
        };

        double loud = rmsDb switch
        {
            >= -26 and <= -12 => 1,
            < -26 => Math.Max(0, (rmsDb + 36) / 10),
            _ => Math.Max(0, (-6 - rmsDb) / 6)
        };

        var score = dur * loud;
        if (clipFrac > 0.001) score *= 0.3;
        score *= Math.Clamp(1.2 - silenceFrac, 0.2, 1.0);
        return score;
    }

    private static List<ClipInfo> SelectBest(List<ClipInfo> clips, double targetSeconds)
    {
        var picked = new List<ClipInfo>();
        double total = 0;
        foreach (var c in clips.Where(c => c.Score > 0).OrderByDescending(c => c.Score))
        {
            var withGap = total + c.Seconds + (picked.Count > 0 ? GapSeconds : 0);
            if (withGap > HardCapSeconds) continue;
            picked.Add(c);
            total = withGap;
            if (total >= targetSeconds) break;
        }
        return picked;
    }

    private static List<ClipInfo> ScanPickList(string folder, string listPath)
    {
        if (!File.Exists(listPath))
        {
            Console.WriteLine($"Pick list not found: {listPath}");
            return new List<ClipInfo>();
        }

        var picked = new List<ClipInfo>();
        double total = 0;
        foreach (var raw in File.ReadAllLines(listPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // ffmpeg concat syntax: file 'name.wav'
            if (line.StartsWith("file ", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim().Trim('\'', '"');

            var path = Path.IsPathRooted(line) ? line : Path.Combine(folder, line);
            if (!File.Exists(path)) { Console.WriteLine($"  (missing, skipped: {line})"); continue; }

            ClipInfo? info;
            try { info = Analyze(path, out var tooLong); if (tooLong) { Console.WriteLine($"  (over {MaxClipSeconds}s, skipped: {line})"); continue; } }
            catch { Console.WriteLine($"  (unreadable, skipped: {line})"); continue; }
            if (info is null) continue;

            var withGap = total + info.Seconds + (picked.Count > 0 ? GapSeconds : 0);
            if (withGap > HardCapSeconds)
            {
                Console.WriteLine($"  (cap reached at {total:F1}s — remaining picks ignored)");
                break;
            }
            picked.Add(info);
            total = withGap;
        }
        return picked;
    }

    // ---- Assemble ----

    private static void Assemble(List<ClipInfo> selection, string outPath)
    {
        var readers = new List<AudioFileReader>();
        try
        {
            var providers = new List<ISampleProvider>();
            for (int i = 0; i < selection.Count; i++)
            {
                var clip = selection[i];
                var reader = new AudioFileReader(clip.Path);
                readers.Add(reader);

                ISampleProvider s = reader;
                if (s.WaveFormat.Channels == 2)
                    s = new StereoToMonoSampleProvider(s) { LeftVolume = 0.5f, RightVolume = 0.5f };
                if (s.WaveFormat.SampleRate != TargetRate)
                    s = new WdlResamplingSampleProvider(s, TargetRate);

                // Per-clip loudness normalization to TargetRmsDb, never pushing the peak past -1 dBFS.
                var rmsGain = Math.Pow(10, (TargetRmsDb - clip.RmsDb) / 20);
                var peakGain = clip.Peak > 0 ? PeakCeiling / clip.Peak : 1;
                s = new VolumeSampleProvider(s) { Volume = (float)Math.Min(rmsGain, peakGain) };

                if (i > 0)
                    s = new OffsetSampleProvider(s) { DelayBy = TimeSpan.FromSeconds(GapSeconds) };
                providers.Add(s);
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WaveFileWriter.CreateWaveFile16(outPath, new ConcatenatingSampleProvider(providers));
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }

    // ---- Reports ----

    private static void WriteScanCsv(List<ClipInfo> clips, string csvPath)
    {
        var sb = new StringBuilder("file,seconds,sampleRate,channels,rmsDb,peakDb,clipFrac,silenceFrac,score\n");
        foreach (var c in clips.OrderByDescending(c => c.Score))
            sb.AppendLine(string.Join(',',
                Path.GetFileName(c.Path),
                c.Seconds.ToString("F2", CultureInfo.InvariantCulture),
                c.SampleRate, c.Channels,
                c.RmsDb.ToString("F1", CultureInfo.InvariantCulture),
                c.PeakDb.ToString("F1", CultureInfo.InvariantCulture),
                c.ClipFrac.ToString("F4", CultureInfo.InvariantCulture),
                c.SilenceFrac.ToString("F2", CultureInfo.InvariantCulture),
                c.Score.ToString("F3", CultureInfo.InvariantCulture)));
        File.WriteAllText(csvPath, sb.ToString());
        Console.WriteLine($"Scan report (full dataset inventory): {csvPath}");
    }

    private static void WriteManifest(List<ClipInfo> selection, string outPath, double totalSeconds)
    {
        var manifestPath = Path.ChangeExtension(outPath, null) + "_manifest.txt";
        var sb = new StringBuilder();
        sb.AppendLine($"voice-ref manifest — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"output: {Path.GetFullPath(outPath)}  ({totalSeconds:F1}s, {TargetRate}Hz mono 16-bit, {GapSeconds * 1000:F0}ms gaps)");
        sb.AppendLine($"clips ({selection.Count}), in order:");
        for (int i = 0; i < selection.Count; i++)
        {
            var c = selection[i];
            sb.AppendLine($"  {i + 1,2}. {Path.GetFileName(c.Path)}  {c.Seconds:F1}s  {c.RmsDb:F1}dBFS  score {c.Score:F2}");
        }
        sb.AppendLine();
        sb.AppendLine("To hand-pick instead: copy lines into picks.txt (one filename per line) and re-run with --list picks.txt");
        File.WriteAllText(manifestPath, sb.ToString());
        Console.WriteLine($"Manifest: {manifestPath}");
    }

    // ---- xAI custom-voice API (terminal replacement for the console UI) ----

    private const string ApiBase = "https://api.x.ai/v1";

    private static int Usage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  voice-ref <clipFolder> [--out file.wav] [--target seconds] [--list picks.txt] [--scan-only]");
        Console.WriteLine("  voice-ref clone [ref.wav] [--name Valerie]   upload reference → create custom voice");
        Console.WriteLine("  voice-ref voices                             list custom voices");
        Console.WriteLine("  voice-ref delete <voice_id>                  delete a custom voice");
        return 1;
    }

    private static HttpClient? CreateApiClient()
    {
        var key = XaiKeyVault.Resolve("XAI_VALERIE_API_KEY", "XAI_VOICE_API_KEY", "XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("No xAI key found (expected XAI_VALERIE_API_KEY in A:\\env\\xai-keys.dpapi or env).");
            return null;
        }
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    private static async Task<int> CloneAsync(string[] args)
    {
        var wavPath = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "voice_ref.wav";
        var name = "Valerie";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--name") name = args[i + 1];

        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"Reference wav not found: {wavPath}");
            return 1;
        }

        try
        {
            using var check = new AudioFileReader(wavPath);
            if (check.TotalTime.TotalSeconds > 120)
            {
                Console.WriteLine($"Reference is {check.TotalTime.TotalSeconds:F1}s — the API rejects anything over 120s.");
                return 1;
            }
            Console.WriteLine($"Uploading {wavPath} ({check.TotalTime.TotalSeconds:F1}s) as \"{name}\" ...");
        }
        catch { /* not fatal — let the API validate the file */ }

        using var http = CreateApiClient();
        if (http is null) return 1;

        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(wavPath));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent("en"), "language");
        form.Add(new StringContent("female"), "gender");
        form.Add(new StringContent("warm"), "tone");
        form.Add(new StringContent("conversational"), "use_case");

        using var resp = await http.PostAsync($"{ApiBase}/custom-voices", form);
        var raw = await resp.Content.ReadAsStringAsync();

        if ((int)resp.StatusCode == 403)
        {
            Console.WriteLine("403 — the create-voice API endpoint is gated to Enterprise plans for this team.");
            Console.WriteLine("The voice must be created through the console UI instead.");
            Console.WriteLine($"Detail: {Truncate(raw, 300)}");
            return 1;
        }
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Create failed {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(raw, 400)}");
            return 1;
        }

        var node = JsonNode.Parse(raw);
        var id = node?["voice_id"]?.GetValue<string>() ?? "?";
        Console.WriteLine();
        Console.WriteLine("=============================================");
        Console.WriteLine($"  VOICE CREATED — voice_id: {id}");
        Console.WriteLine("=============================================");
        Console.WriteLine($"  Name: {node?["name"]?.GetValue<string>() ?? name}");
        Console.WriteLine();
        Console.WriteLine("Use it in appsettings.json — set both:");
        Console.WriteLine($"  Tts:Voice      → \"{id}\"");
        Console.WriteLine($"  Realtime:Voice → \"{id}\"");
        return 0;
    }

    private static async Task<int> ListVoicesAsync()
    {
        using var http = CreateApiClient();
        if (http is null) return 1;

        using var resp = await http.GetAsync($"{ApiBase}/custom-voices?limit=100");
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"List failed {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(raw, 400)}");
            return 1;
        }

        var voices = JsonNode.Parse(raw)?["voices"]?.AsArray();
        if (voices is null || voices.Count == 0)
        {
            Console.WriteLine("No custom voices on this team yet.");
            return 0;
        }

        Console.WriteLine($"{voices.Count} custom voice(s):");
        foreach (var v in voices)
            Console.WriteLine($"  {v?["voice_id"]?.GetValue<string>(),-10}  {v?["name"]?.GetValue<string>()}  ({v?["created_at"]?.GetValue<string>()})");
        return 0;
    }

    private static async Task<int> DeleteVoiceAsync(string voiceId)
    {
        using var http = CreateApiClient();
        if (http is null) return 1;

        using var resp = await http.DeleteAsync($"{ApiBase}/custom-voices/{voiceId}");
        var raw = await resp.Content.ReadAsStringAsync();
        Console.WriteLine(resp.IsSuccessStatusCode
            ? $"Deleted {voiceId}."
            : $"Delete failed {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(raw, 300)}");
        return resp.IsSuccessStatusCode ? 0 : 1;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
