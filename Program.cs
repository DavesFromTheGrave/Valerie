using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Valerie.Models;
using Valerie.Security;
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

            // Stream V's reply token-by-token
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("V: ");
            Console.ResetColor();

            string reply;
            try
            {
                reply = await llm.StreamChatAsync(conversation, token => Console.Write(token));
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"[V is unreachable: {ex.Message}]");
                conversation.RemoveAt(conversation.Count - 1); // drop the failed user turn
                continue;
            }
            Console.WriteLine();

            conversation.Add(new ChatMessage("assistant", reply));

            // Did V decide to share a selfie?
            string? photoPrompt = null;
            var tag = SendPhotoTag.Match(reply);
            if (tag.Success) photoPrompt = tag.Groups["prompt"].Value.Trim();

            // Speak the reply (tag + markdown stripped). Auto-plays the returned MP3.
            var spoken = CleanForVoice(reply);
            if (!string.IsNullOrWhiteSpace(spoken))
                await tts.SpeakAsync(spoken);

            // Fire the selfie: V's own decision, or the user explicitly asked for one.
            if (photoPrompt is { Length: > 0 })
                await images.GenerateAsync(photoPrompt);
            else if (userAskedForPhoto)
                await images.GenerateAsync("a selfie of Valerie — " + input);
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
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
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
