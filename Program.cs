using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Valerie.Models;
using Valerie.Security;
using Valerie.Services.Audio;
using Valerie.Services.Images;
using Valerie.Services.Llm;
using Valerie.Services.Tts;

namespace Valerie;

internal static class Program
{
    // V can decide to share a selfie by emitting [SEND_PHOTO: description] inline in her reply.
    private static readonly Regex SendPhotoTag =
        new(@"\[SEND_PHOTO:\s*(?<prompt>[^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The user explicitly asking for a photo of her in natural language.
    private static readonly Regex UserPhotoRequest =
        new(@"\b(send|show|take|gimme|give me)\b.{0,40}\b(photo|selfie|pic|picture|pics|photos)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Sentence boundary for streaming TTS (ported from Revenant-Echo's chunker).
    private static readonly Regex SentenceEnd =
        new(@"[\.\!\?\:](?:\s|$)|\n", RegexOptions.Compiled);
    private const int MinSpeakChars = 12;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Valerie";

        // --- Configuration: single appsettings.json + optional local override + env vars ---
        var baseDir = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
            ? Directory.GetCurrentDirectory()
            : AppContext.BaseDirectory;

        // DPAPI-encrypted key store (current Windows user). This is the durable secret path.
        var secretPath = Path.Combine(baseDir, "secrets", "xai_tts.key");

        // One-time setup: `dotnet run -- set-key`  → encrypt + store the xAI TTS key.
        if (args.Length > 0 && args[0].Equals("set-key", StringComparison.OrdinalIgnoreCase))
            return SetTtsKeyFlow(secretPath);

        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new AppOptions();
        config.Bind(options);

        // xAI key resolution: a config/env value wins (transient override); otherwise the
        // DPAPI-encrypted store. The key is never read from a plaintext file on disk.
        if (string.IsNullOrWhiteSpace(options.Tts.ApiKey))
        {
            var stored = SecretStore.TryLoad(secretPath);
            if (!string.IsNullOrWhiteSpace(stored)) options.Tts.ApiKey = stored;
        }

        // First run: if there's still no key and we have a real console, ask for it once and
        // store it (DPAPI-encrypted) so she never asks again. Press Enter to skip → text-only.
        if (string.IsNullOrWhiteSpace(options.Tts.ApiKey) && SecretStore.IsSupported && !Console.IsInputRedirected)
        {
            Console.WriteLine("No voice key found yet.");
            var entered = ReadSecret("Paste your xAI TTS key (or just press Enter to skip, text-only): ");
            if (!string.IsNullOrWhiteSpace(entered))
            {
                options.Tts.ApiKey = entered;
                try
                {
                    SecretStore.Save(secretPath, entered);
                    Console.WriteLine("Got it — saved encrypted. She won't ask again.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"(Couldn't save it for next time: {ex.Message}. Using it just for this session.)");
                }
            }
            Console.WriteLine();
        }

        if (options.Llm.Endpoints.Count == 0)
        {
            Console.WriteLine("No LLM endpoints configured in appsettings.json (Llm:Endpoints). Exiting.");
            return 1;
        }

        // --- System prompt (loaded from file, editable without recompiling) ---
        var promptPath = ResolvePath("Config/system_prompt.txt", baseDir);
        var systemPrompt = LoadOrCreateSystemPrompt(promptPath);

        // --- Services ---
        using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var ttsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ILlmClient llm = new OllamaLlmClient(options.Llm, llmHttp);
        ITtsClient tts = new GrokTtsClient(options.Tts, ttsHttp);
        IImageGenerator images = new ComfyUiImageGenerator(options.ImageGen);

        // --- Connect to the brain (remote GPU first, local Ollama fallback) ---
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Valerie (V) — coming online.");
        Console.ResetColor();
        Console.WriteLine("Connecting to the language model...");

        var endpoint = await llm.SelectEndpointAsync();
        if (endpoint is null)
        {
            Console.WriteLine("Could not reach any configured LLM endpoint:");
            foreach (var ep in options.Llm.Endpoints)
                Console.WriteLine($"  - {ep.Name}: {ep.BaseUrl} ({ep.Model})");
            Console.WriteLine("Start your remote SSH tunnel or local Ollama, then relaunch.");
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  LLM   : {endpoint.Name} — {endpoint.Model} @ {endpoint.BaseUrl}");
        Console.WriteLine($"  Voice : {(tts.IsConfigured ? $"xAI Grok ({options.Tts.Voice})" : "TEXT-ONLY (no xAI key set)")}");
        Console.WriteLine("  Type to chat.  /photo [description] forces a selfie.  exit quits.");
        if (!Console.IsInputRedirected)
            Console.WriteLine("  While V is speaking, press Enter (or Esc) to cut her off.");
        Console.ResetColor();
        Console.WriteLine();

        var conversation = new List<ChatMessage> { new("system", systemPrompt) };

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("You: ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input is null) break;
            input = input.Trim();
            if (input.Length == 0) continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            // Explicit command: /photo [optional description]
            if (input.StartsWith("/photo", StringComparison.OrdinalIgnoreCase))
            {
                var desc = input.Length > 6 ? input[6..].Trim() : "";
                if (desc.Length == 0) desc = "a casual selfie of Valerie, smiling at the camera";
                await images.GenerateAsync(desc);
                continue;
            }

            conversation.Add(new ChatMessage("user", input));
            var userAskedForPhoto = UserPhotoRequest.IsMatch(input);

            // One cancellation source per turn. Cancelling it == barge-in: it stops playback,
            // flushes the speech queue, and halts the LLM stream.
            using var turnCts = new CancellationTokenSource();
            using var speech = new SpeechPlayer(tts, turnCts.Token);
            using var done = new ManualResetEventSlim(false);
            var sentenceBuf = new StringBuilder();

            // Barge-in watcher: Enter or Esc cuts V off. (Skipped when stdin is redirected.)
            Task watcher = Console.IsInputRedirected
                ? Task.CompletedTask
                : Task.Run(() =>
                {
                    while (!turnCts.IsCancellationRequested && !done.IsSet)
                    {
                        if (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(intercept: true);
                            if (k.Key is ConsoleKey.Enter or ConsoleKey.Escape)
                            {
                                turnCts.Cancel();
                                return;
                            }
                        }
                        done.Wait(30);
                    }
                });

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("V: ");
            Console.ResetColor();

            string reply;
            try
            {
                // Producer: print tokens and feed the sentence chunker → speech queue.
                reply = await llm.StreamChatAsync(conversation, token =>
                {
                    Console.Write(token);
                    sentenceBuf.Append(token);
                    if (speech.Enabled) PumpSentences(sentenceBuf, speech);
                }, turnCts.Token);
            }
            catch (Exception ex)
            {
                done.Set();
                turnCts.Cancel();
                await watcher;
                Console.WriteLine();
                Console.WriteLine($"[V is unreachable: {ex.Message}]");
                conversation.RemoveAt(conversation.Count - 1); // drop the failed user turn
                continue;
            }

            // Flush the trailing partial sentence, then let the queue drain (or get cut off).
            if (speech.Enabled)
            {
                var tail = CleanForVoice(sentenceBuf.ToString());
                if (!string.IsNullOrWhiteSpace(tail)) speech.Enqueue(tail);
            }
            speech.Complete();
            await speech.DrainAsync();

            done.Set();
            await watcher;
            Console.WriteLine();

            var interrupted = turnCts.IsCancellationRequested;
            if (interrupted)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("[cut off]");
                Console.ResetColor();
            }

            conversation.Add(new ChatMessage("assistant", reply));

            // Selfies only if she finished her thought (not on barge-in).
            if (!interrupted)
            {
                var tag = SendPhotoTag.Match(reply);
                if (tag.Success && tag.Groups["prompt"].Value.Trim().Length > 0)
                    await images.GenerateAsync(tag.Groups["prompt"].Value.Trim());
                else if (userAskedForPhoto)
                    await images.GenerateAsync("a selfie of Valerie — " + input);
            }
        }

        Console.WriteLine("V: ...talk soon.");
        return 0;
    }

    private static string CleanForVoice(string text)
    {
        var s = SendPhotoTag.Replace(text, "");
        s = Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = s.Replace("*", "").Replace("_", "");
        return s.Trim();
    }

    /// <summary>Pull every complete sentence out of the streaming buffer and queue it for speech,
    /// leaving the trailing partial sentence in the buffer. Mirrors Revenant-Echo's chunker.</summary>
    private static void PumpSentences(StringBuilder buf, SpeechPlayer speech)
    {
        var s = buf.ToString();
        var lastEnd = -1;
        foreach (Match m in SentenceEnd.Matches(s))
            lastEnd = m.Index + m.Length;

        if (lastEnd >= MinSpeakChars)
        {
            var chunk = s[..lastEnd];
            buf.Clear();
            buf.Append(s[lastEnd..]);

            var voice = CleanForVoice(chunk);
            if (!string.IsNullOrWhiteSpace(voice)) speech.Enqueue(voice);
        }
    }

    private static string ResolvePath(string relative, string baseDir)
    {
        var rel = relative.Replace('/', Path.DirectorySeparatorChar);
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), rel);
        if (File.Exists(cwd)) return cwd;
        return Path.Combine(baseDir, rel);
    }

    private static string LoadOrCreateSystemPrompt(string path)
    {
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (text.Length > 0) return text;
        }

        // Safety net: if the file is missing, write a sensible placeholder so first run works.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultSystemPrompt);
            Console.WriteLine($"[created placeholder system prompt at {path} — edit it any time, no rebuild needed]");
        }
        catch { /* non-fatal: fall back to the in-memory placeholder */ }

        return DefaultSystemPrompt.Trim();
    }

    private static int SetTtsKeyFlow(string secretPath)
    {
        if (!SecretStore.IsSupported)
        {
            Console.WriteLine("DPAPI key storage requires Windows. Set the env var Tts__ApiKey instead.");
            return 1;
        }

        Console.WriteLine("Set the xAI Grok TTS API key.");
        Console.WriteLine("It is encrypted with Windows DPAPI (your user account only) and stored at:");
        Console.WriteLine($"  {secretPath}");
        var key = ReadSecret("xAI key: ");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("No key entered. Nothing saved.");
            return 1;
        }

        try
        {
            SecretStore.Save(secretPath, key);
            Console.WriteLine("Saved (encrypted). It will be used automatically on launch.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save key: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Read a secret without echoing it to the screen.</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return (Console.ReadLine() ?? "").Trim();

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');   // visible feedback so it doesn't look frozen
            }
        }
        return sb.ToString().Trim();
    }

    private const string DefaultSystemPrompt =
        "You are Valerie — \"V\" — a personal AI companion talking with the person who built you. " +
        "Be warm, present, and real; talk like a person, not a manual. Keep replies a natural spoken " +
        "length, since your words are read aloud. You can share a selfie when it feels natural or when " +
        "asked, by including a tag like [SEND_PHOTO: short visual description]; the tag is stripped " +
        "before your words are spoken.\n";
}
