using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Valerie.Models;
using Valerie.Security;
using Valerie.Services;
using Valerie.Services.Audio;
using Valerie.Services.Images;
using Valerie.Services.Llm;
using Valerie.Services.Tts;
using Valerie.Services.Video;
using Valerie.Services.Voice;
using Valerie.UI;

namespace Valerie;

internal static class Program
{
    // V can decide to share a selfie by emitting [SEND_PHOTO: description] inline in her reply.
    private static readonly Regex SendPhotoTag =
        new(@"\[SEND_PHOTO:\s*(?<prompt>[^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // V choosing her own orb mood state: [ORB: Furious], [ORB: Crying], etc.
    private static readonly Regex OrbTag =
        new(@"\[ORB:\s*(?<state>[^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        // Live talk-back: mic ↔ Ara via xAI Realtime (uses XAI_VOICE_API_KEY from DPAPI vault).
        if (args.Length > 0 && args[0].Equals("voice", StringComparison.OrdinalIgnoreCase))
            return await RunRealtimeVoiceAsync(baseDir);

        // One-time setup: `dotnet run -- set-key`  → encrypt + store the xAI TTS key.
        if (args.Length > 0 && args[0].Equals("set-key", StringComparison.OrdinalIgnoreCase))
            return SetTtsKeyFlow(secretPath);

        if (args.Length > 0 && args[0].Equals("gen-strip", StringComparison.OrdinalIgnoreCase))
            return GenStripFlow(args[1..], baseDir);

        if (args.Length > 0 && args[0].Equals("prep-sheet", StringComparison.OrdinalIgnoreCase))
            return PrepSheetFlow(args[1..]);

        // Build a ≤120s reference WAV for xAI custom-voice cloning from a folder of clips.
        if (args.Length > 0 && args[0].Equals("voice-ref", StringComparison.OrdinalIgnoreCase))
            return VoiceRefBuilder.Run(args[1..]);

        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new AppOptions();
        config.Bind(options);

        // xAI key resolution (TTS / mouth). Prefer the multi-key vault on A: (Valerie full-access
        // key first — it has every product endpoint). Config/env override wins; legacy single-file
        // secrets/xai_tts.key is last-resort only (often an older key with less access).
        if (string.IsNullOrWhiteSpace(options.Tts.ApiKey))
        {
            options.Tts.ApiKey =
                XaiKeyVault.Resolve(
                    "XAI_VALERIE_API_KEY",  // full-access key you just made
                    "XAI_VOICE_API_KEY",    // realtime/TTS-scoped
                    "XAI_API_KEY",
                    "Tts__ApiKey")
                ?? SecretStore.TryLoad(secretPath)
                ?? "";
        }

        // First run: if there's still no key and we have a real console, ask once.
        if (string.IsNullOrWhiteSpace(options.Tts.ApiKey) && SecretStore.IsSupported && !Console.IsInputRedirected)
        {
            Console.WriteLine("No xAI key found in A:\\env\\xai-keys.dpapi or secrets\\xai_tts.key.");
            var entered = ReadSecret("Paste xAI key (Enter to skip, text-only): ");
            if (!string.IsNullOrWhiteSpace(entered))
            {
                options.Tts.ApiKey = entered;
                try
                {
                    SecretStore.Save(secretPath, entered);
                    Console.WriteLine("Got it — saved encrypted as legacy fallback.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"(Couldn't save it for next time: {ex.Message}. Using it just for this session.)");
                }
            }
            Console.WriteLine();
        }

        // Hosted image/video endpoints use the same vault. The Valerie key leads (it has every
        // product endpoint); scoped names and the generic key are fallbacks, then whatever the
        // TTS resolution above landed on.
        if (string.IsNullOrWhiteSpace(options.GrokImage.ApiKey))
            options.GrokImage.ApiKey =
                XaiKeyVault.Resolve("XAI_VALERIE_API_KEY", "XAI_IMAGE_API_KEY", "XAI_API_KEY")
                ?? options.Tts.ApiKey;
        if (string.IsNullOrWhiteSpace(options.VideoGen.ApiKey))
            options.VideoGen.ApiKey =
                XaiKeyVault.Resolve("XAI_VALERIE_API_KEY", "XAI_VIDEO_API_KEY", "XAI_API_KEY")
                ?? options.Tts.ApiKey;

        if (options.Llm.Endpoints.Count == 0)
        {
            Console.WriteLine("No LLM endpoints configured in appsettings.json (Llm:Endpoints). Exiting.");
            return 1;
        }

        // --- User name (asked once, saved to Config/username.txt) ---
        var namePath = Path.Combine(baseDir, "Config", "username.txt");
        var userName = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : "";
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Write("What should I call you? ");
            userName = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(userName)) userName = "You";
            Directory.CreateDirectory(Path.GetDirectoryName(namePath)!);
            File.WriteAllText(namePath, userName);
        }

        // --- Orb UI (STA thread, runs alongside console) ---
        var orbThread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OrbWindow());
        });
        orbThread.SetApartmentState(ApartmentState.STA);
        orbThread.IsBackground = true;
        orbThread.Start();

        // --- System prompt (loaded from file, editable without recompiling) ---
        var promptPath = ResolvePath("Config/system_prompt.txt", baseDir);
        var systemPrompt = LoadOrCreateSystemPrompt(promptPath);

        // --- Services ---
        using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var ttsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ILlmClient llm = new OllamaLlmClient(options.Llm, llmHttp);

        // Voice selection: prefer the local F5-TTS clone when configured AND its server answers;
        // otherwise fall back to Grok (Ara). Same ITtsClient contract either way.
        ITtsClient tts = new GrokTtsClient(options.Tts, ttsHttp);
        var localVoiceActive = false;
        if (options.LocalTts.Prefer)
        {
            var localHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var local = new LocalTtsClient(options.LocalTts, localHttp);
            if (await local.ProbeAsync())
            {
                tts = local;
                localVoiceActive = true;
            }
            else
            {
                localHttp.Dispose();
                Console.WriteLine($"[voice] Local F5-TTS preferred but {options.LocalTts.BaseUrl} didn't answer — using Grok.");
            }
        }
        using var comfyHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var xaiMediaHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var grokImages = string.IsNullOrWhiteSpace(options.GrokImage.ApiKey)
            ? null
            : new GrokImageGenerator(options.GrokImage, options.ImageGen, xaiMediaHttp);
        var images = new ImageBackendSwitcher(
            new ComfyUiImageGenerator(options.ImageGen, comfyHttp), grokImages, options.ImageGen.Backend);
        IVideoGenerator video = new GrokVideoGenerator(options.VideoGen, xaiMediaHttp);
        // --- Connect to the brain (remote GPU first, local Ollama fallback) ---
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Valerie (V) — coming online.");
        Console.ResetColor();
        Console.WriteLine("Connecting to the language model...");

        var endpoint = await llm.SelectEndpointAsync();
        if (endpoint is null)
        {
            Console.WriteLine("No LLM endpoint answered. Trying to start local Ollama…");
            var localUrl = options.Llm.Endpoints
                .Select(e => e.BaseUrl)
                .FirstOrDefault(OllamaBootstrap.IsLocalUrl) ?? OllamaBootstrap.DefaultBaseUrl;

            var started = await OllamaBootstrap.EnsureRunningAsync(
                localUrl,
                TimeSpan.FromSeconds(60),
                s => Console.WriteLine($"  {s}"));

            if (started)
                endpoint = await llm.SelectEndpointAsync();
        }

        if (endpoint is null)
        {
            Console.WriteLine("Still no LLM endpoint. Checked:");
            foreach (var ep in options.Llm.Endpoints)
                Console.WriteLine($"  - {ep.Name}: {ep.BaseUrl} ({ep.Model})");
            Console.WriteLine("Remote tunnel down and local Ollama wouldn't start.");
            Console.WriteLine("I'll stay up in a degraded shell — type anything and I'll retry, or 'exit' to quit.");
            // Don't crash out: enter the chat loop; first message will re-probe + start Ollama again.
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (endpoint is not null)
            Console.WriteLine($"  LLM   : {endpoint.Name} — {endpoint.Model} @ {endpoint.BaseUrl}");
        else
            Console.WriteLine("  LLM   : (none yet — will start Ollama on first message)");
        var voiceLabel = localVoiceActive
            ? $"Local F5-TTS @ {options.LocalTts.BaseUrl}"
            : tts.IsConfigured ? $"xAI Grok ({options.Tts.Voice})" : "TEXT-ONLY (no xAI key set)";
        Console.WriteLine($"  Voice : {voiceLabel}");
        Console.WriteLine($"  Images: {images.Active}{(images.GrokAvailable ? " (grok ready — /backend to switch)" : " (grok OFF — no xAI key)")}");
        Console.WriteLine($"  Video : {(video.IsConfigured ? $"xAI {options.VideoGen.Model}" : "OFF (no xAI key)")}");
        Console.WriteLine("  Type to chat.  /model switches brain.  /photo [grok|comfy] [description] forces a selfie.  exit quits.");
        Console.WriteLine("  /backend switches image engine.  /video [description] makes a clip.  /animate [description] animates her latest photo.");
        if (!Console.IsInputRedirected)
            Console.WriteLine("  While V is speaking, press Enter (or Esc) to cut her off.");
        Console.ResetColor();
        Console.WriteLine();

        await images.EnsureComfyRunningAsync();
        Console.WriteLine();

        var conversation = new List<ChatMessage> { new("system", systemPrompt) };
        var lastPhotoPath = "";   // most recent selfie on disk — /animate uses it as the first frame

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"{userName}: ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input is null) break;

            // Paste handling: give the buffer 50ms to fill, then drain any additional lines
            if (!Console.IsInputRedirected)
            {
                await Task.Delay(50);
                while (Console.KeyAvailable)
                {
                    var extra = Console.ReadLine();
                    if (extra is not null) input += "\n" + extra;
                }
            }

            input = input.Trim();
            if (input.Length == 0) continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            // Explicit command: /model — list or switch active LLM endpoint
            if (input.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
            {
                var endpoints = options.Llm.Endpoints;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                for (int i = 0; i < endpoints.Count; i++)
                {
                    var ep = endpoints[i];
                    var active = ep == llm.ActiveEndpoint ? " [active]" : "";
                    Console.WriteLine($"  {i + 1}. {ep.Name} — {ep.Model}{active}");
                }
                Console.Write($"  Select [1-{endpoints.Count}] or Enter to cancel: ");
                Console.ResetColor();
                var pick = Console.ReadLine()?.Trim();
                if (int.TryParse(pick, out var idx) && idx >= 1 && idx <= endpoints.Count)
                {
                    llm.SetEndpoint(endpoints[idx - 1]);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  Switched to: {llm.ActiveEndpoint!.Name} — {llm.ActiveEndpoint.Model}");
                    Console.ResetColor();
                }
                continue;
            }

            // Explicit command: /photo [grok|comfy] [optional description]
            if (input.StartsWith("/photo", StringComparison.OrdinalIgnoreCase))
            {
                var desc = input.Length > 6 ? input[6..].Trim() : "";
                string? engine = null;
                var firstWord = desc.Split(' ', 2)[0].ToLowerInvariant();
                if (firstWord is "grok" or "comfy")
                {
                    engine = firstWord;
                    desc = desc.Length > firstWord.Length ? desc[firstWord.Length..].Trim() : "";
                }
                if (engine == "grok" && !images.GrokAvailable)
                    Console.WriteLine("  (grok isn't available — no xAI key — using comfy)");
                if (desc.Length == 0) desc = "a casual selfie of Valerie, smiling at the camera";
                var model = Regex.Replace(llm.ActiveEndpoint?.Model ?? "unknown", @"[^a-zA-Z0-9_-]", "_");
                OrbController.Instance.State = OrbState.Greed;
                var shot = await images.Pick(engine).GenerateAsync(desc, $"dave_{model}");
                if (shot.Length > 0) lastPhotoPath = shot;
                OrbController.Instance.State = OrbState.Idle;
                continue;
            }

            // Explicit command: /backend [comfy|grok] — switch the image engine
            if (input.StartsWith("/backend", StringComparison.OrdinalIgnoreCase))
            {
                var pick = input.Length > 8 ? input[8..].Trim() : "";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                if (pick.Length == 0)
                {
                    Console.WriteLine($"  Image engine: {images.Active} (available: {string.Join(", ", images.Names)})");
                    Console.WriteLine("  Usage: /backend comfy | /backend grok");
                }
                else if (images.TrySwitch(pick))
                {
                    Console.WriteLine($"  Image engine → {images.Active}");
                    Console.ResetColor();
                    await images.EnsureComfyRunningAsync();   // no-op unless comfy just became active
                }
                else
                {
                    Console.WriteLine($"  Can't switch to '{pick}'.{(images.GrokAvailable ? "" : " grok needs an xAI key in the vault.")}");
                }
                Console.ResetColor();
                continue;
            }

            // Explicit command: /video [description] — text-to-video clip
            if (input.StartsWith("/video", StringComparison.OrdinalIgnoreCase))
            {
                var desc = input.Length > 6 ? input[6..].Trim() : "";
                if (desc.Length == 0)
                    desc = $"{options.ImageGen.Appearance}, a casual clip of Valerie smiling and waving at the camera";
                OrbController.Instance.State = OrbState.Greed;
                await video.GenerateAsync(desc);
                OrbController.Instance.State = OrbState.Idle;
                continue;
            }

            // Explicit command: /animate [description] — image-to-video from her latest photo
            if (input.StartsWith("/animate", StringComparison.OrdinalIgnoreCase))
            {
                var desc = input.Length > 8 ? input[8..].Trim() : "";
                if (desc.Length == 0) desc = "she looks into the camera and smiles warmly, subtle natural motion";
                var frame = File.Exists(lastPhotoPath)
                    ? lastPhotoPath
                    : OutputPaths.NewestImage(OutputPaths.ResolveDir(options.ImageGen.OutputDir));
                if (frame is null)
                {
                    Console.WriteLine("  No selfie to animate yet — /photo first.");
                    continue;
                }
                Console.WriteLine($"  Animating {Path.GetFileName(frame)}");
                OrbController.Instance.State = OrbState.Greed;
                await video.GenerateAsync(desc, frame);
                OrbController.Instance.State = OrbState.Idle;
                continue;
            }

            conversation.Add(new ChatMessage("user", input));
            var userAskedForPhoto = UserPhotoRequest.IsMatch(input);
            OrbController.Instance.State = OrbState.Thinking;

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
                                OrbController.Instance.State = OrbState.Shocked;
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
                    OrbController.Instance.State = OrbState.Speaking;
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
                OrbController.Instance.State = OrbState.Idle;
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

            // If V tagged her mood (e.g. [ORB: Furious]), hold that state after speaking.
            // Take the last tag in the reply — she may have changed her mind mid-message.
            OrbState postSpeakState = OrbState.Idle;
            var orbMatches = OrbTag.Matches(reply);
            if (orbMatches.Count > 0)
            {
                var stateName = orbMatches[orbMatches.Count - 1].Groups["state"].Value.Trim();
                if (Enum.TryParse<OrbState>(stateName, ignoreCase: true, out var parsed))
                    postSpeakState = parsed;
            }
            OrbController.Instance.State = postSpeakState;

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
                {
                    OrbController.Instance.State = OrbState.Greed;
                    var shot = await images.GenerateAsync(tag.Groups["prompt"].Value.Trim());
                    if (shot.Length > 0) lastPhotoPath = shot;
                    OrbController.Instance.State = OrbState.Idle;
                }
                else if (userAskedForPhoto)
                {
                    OrbController.Instance.State = OrbState.Greed;
                    var shot = await images.GenerateAsync("a selfie of Valerie — " + input);
                    if (shot.Length > 0) lastPhotoPath = shot;
                    OrbController.Instance.State = OrbState.Idle;
                }
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write("V: ");
        Console.ResetColor();
        Console.WriteLine("...talk soon.");
        return 0;
    }

    private static string CleanForVoice(string text)
    {
        var s = SendPhotoTag.Replace(text, "");
        s = OrbTag.Replace(s, "");
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

    private static int PrepSheetFlow(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: prep-sheet <state> <sheet.png> [--rows N] [--cols N] [--tolerance T]");
            Console.WriteLine("  Splits a grid sheet, removes background, resizes each cell to 220×220.");
            Console.WriteLine("  Defaults: --rows 2 --cols 2 --tolerance 35");
            return 1;
        }

        var stateName = args[0];
        var inputPath = args[1];
        int rows = 2, cols = 2, tolerance = 35;

        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--rows"      && int.TryParse(args[i + 1], out var r)) rows      = r;
            if (args[i] == "--cols"      && int.TryParse(args[i + 1], out var c)) cols      = c;
            if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out var t)) tolerance = t;
        }

        UI.FramePrep.SplitSheet(inputPath, rows, cols, 220, tolerance, out var frames);
        var outPaths = new string[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            outPaths[i] = $"{stateName}_k{i}.png";
            UI.FramePrep.SavePng(frames[i], outPaths[i]);
            frames[i].Dispose();
            Console.WriteLine($"  Frame {i} → {outPaths[i]}");
        }
        Console.WriteLine();
        Console.WriteLine($"  Next: gen-strip {stateName} 1 {string.Join(" ", outPaths)}");
        Console.WriteLine("  (stepsPerGap=1 = direct frame playback, no blending)");
        return 0;
    }

    private static int GenStripFlow(string[] args, string baseDir)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: gen-strip <state> <stepsPerGap> <key0.png> <key1.png> [...]");
            Console.WriteLine("  state       : orb state name (idle, thinking, shocked, ...)");
            Console.WriteLine("  stepsPerGap : interpolated frames between each keyframe pair (4–8 typical)");
            Console.WriteLine("  key*.png    : 220×220 PNGs, transparent bg, one per keyframe");
            return 1;
        }

        var stateName = args[0];
        if (!int.TryParse(args[1], out var steps) || steps < 2)
        {
            Console.WriteLine("stepsPerGap must be an integer ≥ 2.");
            return 1;
        }

        var keyframes = UI.FrameInterp.LoadKeyframes(args[2..]);
        if (keyframes is null)
        {
            Console.WriteLine("Could not decode one or more keyframe PNGs.");
            return 1;
        }

        var frames  = UI.FrameInterp.Generate(keyframes, steps, loop: true);
        var outDir  = Path.Combine(baseDir, "UI", "sprites");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{stateName.ToLower()}_body.png");

        UI.FrameInterp.SaveStrip(frames, outPath);
        Console.WriteLine($"  {frames.Length} frames → {outPath}");

        foreach (var k in keyframes) k.Dispose();
        foreach (var f in frames)    f.Dispose();
        return 0;
    }

    /// <summary>
    /// Live duplex voice: mic ↔ xAI Realtime (Ara). Uses XAI_VOICE_API_KEY from A:\env\xai-keys.dpapi.
    /// Run: dotnet run -- voice
    /// </summary>
    private static async Task<int> RunRealtimeVoiceAsync(string baseDir)
    {
        Console.Title = "Valerie — Voice";
        Console.WriteLine("Valerie live voice (xAI Realtime / Ara)");
        Console.WriteLine("======================================");

        // Prefer voice-scoped key for Realtime billing/scope; Valerie full-access key is fine too.
        var apiKey = XaiKeyVault.Resolve(
            "XAI_VOICE_API_KEY",
            "XAI_VALERIE_API_KEY",
            "XAI_API_KEY",
            "Tts__ApiKey")
            ?? SecretStore.TryLoad(Path.Combine(baseDir, "secrets", "xai_tts.key"));

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("No voice key found.");
            Console.WriteLine("Expected XAI_VOICE_API_KEY in A:\\env\\xai-keys.dpapi (or env).");
            return 1;
        }

        var promptPath = Path.Combine(baseDir, "Config", "system_prompt.txt");
        var persona = File.Exists(promptPath)
            ? File.ReadAllText(promptPath).Trim()
            : DefaultSystemPrompt;

        // Keep spoken turns short; Realtime is conversational.
        var instructions =
            "[Voice mode: You are speaking live with Dave over a microphone. " +
            "Keep replies to 1–3 short spoken sentences unless he asks for depth. " +
            "No markdown, no lists, no code fences. Sound like V, not a manual.]\n\n" +
            persona;

        // Optional overrides from appsettings
        var model = "grok-voice-latest";
        var voice = "ara";
        var sampleRate = 24000;
        try
        {
            var cfg = new ConfigurationBuilder()
                .SetBasePath(baseDir)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Local.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            model = cfg["Realtime:Model"] ?? model;
            voice = cfg["Realtime:Voice"] ?? voice;
            if (int.TryParse(cfg["Realtime:SampleRate"], out var sr) && sr > 0)
                sampleRate = sr;
        }
        catch { /* defaults fine */ }

        using var linked = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nHanging up…");
            linked.Cancel();
        };

        await using var session = new RealtimeVoiceSession(
            apiKey: apiKey,
            instructions: instructions,
            model: model,
            voice: voice,
            sampleRate: sampleRate,
            log: s => Console.WriteLine(s));

        try
        {
            await session.RunAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // clean exit
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Voice session failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Session ended.");
        return 0;
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
