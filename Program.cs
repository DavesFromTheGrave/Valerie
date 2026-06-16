using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading;

namespace GraveLorelai
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // === LAUNCHER / BOOT SEQUENCE (game-like experience) ===
            // This is the entry point for the double-click EXE vision.
            // Boots Ollama in background, shows loading, first-run voice/text name collection,
            // then enters the conversational core. Text is for accessibility; the goal is audio-primary.
            // Everything self-contained in this Grave-Lorelai folder so the old Lorelai can be cleaned up.

            Console.Title = "Lorelai";
            Console.WriteLine("Booting Lorelai...");

            // Load .env configuration
            LoadEnv();

            // Ensure local data folder (stays with the EXE/project)
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);

            // Start Ollama server in background if not already running (hidden, like a service)
            if (!IsOllamaRunning())
            {
                Console.WriteLine("Starting Ollama server in the background...");
                try
                {
                    // Find absolute path to ollama.exe on Windows
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string ollamaPath = Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
                    string exeToRun = File.Exists(ollamaPath) ? ollamaPath : "ollama";

                    var ollamaInfo = new ProcessStartInfo
                    {
                        FileName = exeToRun,
                        Arguments = "serve",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = false, // DO NOT redirect stdout to prevent deadlocks
                        RedirectStandardError = false  // DO NOT redirect stderr to prevent deadlocks
                    };
                    Process.Start(ollamaInfo);
                    // Give the server a moment to come up
                    await Task.Delay(2500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not auto-start Ollama ({ex.Message}).");
                    Console.WriteLine("Make sure Ollama is installed and 'ollama' is in your PATH.");
                }
            }

            // Wait for Ollama API to be responsive
            Console.Write("Waiting for Ollama to be ready");
            int waitAttempts = 0;
            while (!IsOllamaRunning() && waitAttempts < 30)
            {
                Console.Write(".");
                await Task.Delay(500);
                waitAttempts++;
            }
            Console.WriteLine(" ready.");

            // Start TTS server if running local mode (no Grok API key)
            if (string.IsNullOrEmpty(GetEnv("GROK_TTS_API_KEY")))
            {
                await EnsureTtsServerRunningAsync();
            }

            // Ensure ComfyUI is running in the background on boot
            await EnsureComfyRunningAsync();

            // Start C# Audio Queue Worker thread
            Task.Run(() => AudioWorker(ttsCts.Token));

            // Simple game-like loading sequence ("screen goes dark" feel via clear + animation)
            Console.Clear();
            Console.WriteLine("Lorelai is coming online...");
            for (int i = 0; i < 12; i++)
            {
                Console.Write(".");
                await Task.Delay(120);
            }
            Console.WriteLine("\n");

            // First-boot name collection (text for now / accessibility; voice input later).
            // On first boot we play a pre-recorded WAV greeting instead of asking the model live.
            // This keeps the experience fast, consistent, and "audio only" by design.
            // Stored locally in the project data dir so it travels with the folder/EXE.
            string nameFile = Path.Combine(dataDir, "user_name.txt");
            string userName;
            bool isFirstBoot = !File.Exists(nameFile);

            if (isFirstBoot)
            {
                // Play pre-recorded first-boot greeting.
                // The audio MUST be sexy and sultry — low, breathy, confident, seductive Lorelai delivery.
                // Exact line to record (keep it short and teasing):
                //   "Hey... what's your name?"
                // Delivery: slow, intimate, slightly husky, with a little smile in the voice. Think sultry whisper that still carries.
                // Place the file at: assets/audio/first_boot_greeting.wav (relative to the EXE or project root).
                // Record it with whatever TTS/voice cloning you're using in Revenant Echo so it matches the final Lorelai voice.
                // WAV works out of the box with SoundPlayer (zero extra dependencies). MP3 would need NAudio.
                // This is the "Lorelei will come over" moment on first boot — pure audio, no model call for the prompt itself.
                string greetingAudioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "audio", "first_boot_greeting.wav");

                if (File.Exists(greetingAudioPath))
                {
                    try
                    {
                        using (var player = new SoundPlayer(greetingAudioPath))
                        {
                            player.PlaySync(); // Blocks until the clip finishes — Lorelai "comes over" via speakers
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"(Could not play greeting audio: {ex.Message})");
                        Console.WriteLine("Falling back to text prompt.");
                    }
                }
                else
                {
                    Console.WriteLine("(First-boot greeting audio not found at assets/audio/first_boot_greeting.wav)");
                    Console.WriteLine("Falling back to text. Create a short WAV of Lorelai asking for the user's name for the real experience.");
                }

                Console.Write("What's your name? ");
                userName = Console.ReadLine()?.Trim() ?? "Friend";
                File.WriteAllText(nameFile, userName);
                Console.WriteLine($"Got it. Nice to meet you, {userName}.");
                // TODO (full audio): After playing the WAV, listen via mic + STT for the name reply.
                // Then have Lorelai greet them properly using the name (via the LLM or another short pre-recorded clip).
            }
            else
            {
                userName = File.ReadAllText(nameFile).Trim();
            }

            // Dynamic model selection based on local Ollama tags
            var (activeModel, enableThinking) = await SelectModelPromptAsync();

            // Existing text core initialization (now after the launcher/boot)
            Console.WriteLine("Initializing Grave-Lorelai (text generation core - audio native path)...");

            string loraInfo = CurrentVisual.Loras != null && CurrentVisual.Loras.Count > 0 
                ? string.Join(" + ", CurrentVisual.Loras.Select(l => $"{l.Name} (x{l.Strength})")) 
                : "none";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Lorelai visual style: Checkpoint={CurrentVisual.Checkpoint}, LoRAs=[{loraInfo}]");
            Console.WriteLine("Type /looks or /help for easy model/LoRA/appearance swapping (full customization without ComfyUI hassle).");
            Console.WriteLine("To FORCE a test photo right now (bypasses the AI deciding): type  /photo close-up selfie in bedroom, soft light, smiling  (or any description)");
            Console.ResetColor();

            // Paths - using the custom Ollama model (persona prompt is baked into the model via modelfile).
            // Lorelai_Persona.txt in personal/prompts/ is the source for the modelfile (and reference).
            // No local model load needed; all via Ollama API.
            // personaPath kept for reference only (prompt is baked into the custom Ollama model)
            // string personaPath = @"M:\Projects\Grave-Lorelai\personal\prompts\Lorelai_Persona.txt"; // for reference / future use

            // (No modelPath or systemPrompt load - handled by the custom Ollama model)

            Console.WriteLine($"\nGrave-Lorelai ready for {userName} (Lorelai persona - spoken words first, via Ollama model {activeModel}).");
            Console.WriteLine("Type message (or use voice when the audio layer is wired). Output is the text for audio/TTS. 'exit' to quit.\n");
            Console.WriteLine("Audio goal: local LLM here for content -> TTS (cloud Ara/Google or future local) -> local images after spoken line.");
            Console.WriteLine("GPU handoff note: LLM on GPU now; later queue ComfyUI to avoid VRAM fight on 3070 Ti.\n");
            if (isFirstBoot)
            {
                Console.WriteLine("First boot complete. On next launch Lorelai will greet you by name through the speakers.");
            }

            // Auto-logging for recording system (starts proper engineering data collection, separated by LLM/persona)
            string logDir = Path.Combine("personal", "chats", "lorelai");
            Directory.CreateDirectory(logDir);
            string sessionFile = Path.Combine(logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            string last_thinking = "";
            var conversation = new List<object>();

            var httpClient = new HttpClient();

            while (true)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("You: ");
                Console.ResetColor();

                string? rawInput = Console.ReadLine();

                // Treat null (EOF / stdin closed / non-interactive terminal) or empty as exit.
                // This is why you sometimes see it print "You: " and immediately shut down:
                // the terminal/session that launched "dotnet run" did not provide a live interactive stdin.
                if (rawInput == null)
                {
                    Console.WriteLine();
                    Console.WriteLine("(No interactive input detected — stdin appears closed or redirected.)");
                    Console.WriteLine("Try opening a fresh PowerShell window directly in this folder (address bar trick or Start > PowerShell) and run 'dotnet run' again.");
                    break;
                }

                string input = rawInput.Trim();

                if (string.IsNullOrEmpty(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                // Intercept visual customization commands first (easy model/LoRA/appearance swap)
                if (HandleVisualCommand(input))
                {
                    continue;  // handled, don't send to LLM
                }

                // Intercept LLM model switching command
                if (input.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
                {
                    var result = await HandleModelCommandAsync(input, httpClient);
                    if (result.switched)
                    {
                        activeModel = result.modelName;
                        enableThinking = result.thinking;
                    }
                    continue;
                }

                // Quick force test for photo generation (bypasses LLM tag - use this to test the image pipeline)
                if (input.Trim().ToLower().StartsWith("/photo "))
                {
                    string scene = input.Trim().Substring(7).Trim();
                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        Console.WriteLine($"[Forcing test photo with scene: {scene}]");
                        _ = Task.Run(() => GenerateLorelaiSelfieAsync(scene));
                    }
                    else
                    {
                        Console.WriteLine("Usage: /photo close-up selfie in my bedroom, wearing cyberpunk outfit, smiling at camera");
                    }
                    continue;
                }

                if (input.Equals("t", StringComparison.OrdinalIgnoreCase) || input.Equals("thoughts", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(last_thinking))
                    {
                        Console.WriteLine("\n--- Lorelai's thoughts ---\n" + last_thinking + "\n---\n");
                    }
                    else
                    {
                        Console.WriteLine("No thoughts for the last turn.");
                    }
                    continue;
                }

                // Add user message to conversation history for proper back-and-forth
                conversation.Add(new { role = "user", content = input });

                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write("Lorelai (spoken text): ");
                Console.ResetColor();

                StringBuilder fullResponse = new StringBuilder();
                StringBuilder thoughtsBuffer = new StringBuilder();
                StringBuilder sentenceBuffer = new StringBuilder();
                
                bool isThinking = false;
                bool thinkingShown = false;
                
                char[] spinner = new char[] { '|', '/', '-', '\\' };
                int spinnerIndex = 0;

                var requestBody = new 
                {
                    model = activeModel,
                    messages = conversation.ToArray(),
                    stream = true,
                    think = enableThinking
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync("http://127.0.0.1:11434/api/chat", content);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                string? line;
                
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var chunk = JsonSerializer.Deserialize<JsonElement>(line);
                    
                    if (chunk.TryGetProperty("message", out var msgElement))
                    {
                        // 1. Check native thinking chunk
                        bool hasThinkingProp = msgElement.TryGetProperty("thinking", out var thinkingElement);
                        if (hasThinkingProp)
                        {
                            string? thinkText = thinkingElement.GetString();
                            if (!string.IsNullOrEmpty(thinkText))
                            {
                                thoughtsBuffer.Append(thinkText);
                                
                                // Show spinner
                                if (!thinkingShown)
                                {
                                    Console.Write("Thinking...  ");
                                    thinkingShown = true;
                                }
                                Console.Write($"\b{spinner[spinnerIndex]}");
                                spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                                continue; // Skip printing to spoken console
                            }
                        }

                        // 2. Check spoken content chunk
                        if (msgElement.TryGetProperty("content", out var contentElement))
                        {
                            string? text = contentElement.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                // If we were showing the spinner, erase it now
                                if (thinkingShown)
                                {
                                    // Backspace, space, backspace to clear the spinner character
                                    Console.Write("\b \b");
                                    thinkingShown = false;
                                }

                                // Check for inline `<think>` tags (fallback/redundancy)
                                string temp = text;
                                if (temp.Contains("<think>"))
                                {
                                    isThinking = true;
                                    int startIdx = temp.IndexOf("<think>");
                                    thoughtsBuffer.Append(temp.Substring(startIdx + 7));
                                    continue;
                                }
                                if (temp.Contains("</think>"))
                                {
                                    isThinking = false;
                                    int endIdx = temp.IndexOf("</think>");
                                    string after = temp.Substring(endIdx + 8);
                                    if (!string.IsNullOrEmpty(after))
                                    {
                                        Console.Write(after);
                                        fullResponse.Append(after);
                                        sentenceBuffer.Append(after);
                                        ProcessSentenceBuffer(ref sentenceBuffer);
                                    }
                                    continue;
                                }

                                if (isThinking)
                                {
                                    thoughtsBuffer.Append(temp);
                                    
                                    // Show spinner for inline thinking too
                                    if (!thinkingShown)
                                    {
                                        Console.Write("Thinking...  ");
                                        thinkingShown = true;
                                    }
                                    Console.Write($"\b{spinner[spinnerIndex]}");
                                    spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                                }
                                else
                                {
                                    // Print spoken content
                                    Console.Write(temp);
                                    fullResponse.Append(temp);
                                    sentenceBuffer.Append(temp);
                                    ProcessSentenceBuffer(ref sentenceBuffer);
                                }
                            }
                        }
                    }
                    if (chunk.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean())
                    {
                        break;
                    }
                }

                // Erase spinner if it was still active
                if (thinkingShown)
                {
                    Console.Write("\b \b");
                }

                Console.WriteLine("\n");

                // Flush remaining sentence buffer
                string remaining = sentenceBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining))
                {
                    ttsQueue.Add(remaining);
                }

                // Extract final strings
                string spoken = fullResponse.ToString().Trim();
                string thoughts = thoughtsBuffer.ToString().Trim();

                // === Lorelai Camera tag handling (after full turn) ===
                string cleanSpoken = spoken;
                if (spoken.Contains(PhotoTag, StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the scene description inside the tag
                    int start = spoken.IndexOf(PhotoTag, StringComparison.OrdinalIgnoreCase);
                    int end = spoken.IndexOf(']', start);
                    string scene = "";
                    if (end > start)
                    {
                        scene = spoken.Substring(start + PhotoTag.Length, end - (start + PhotoTag.Length)).Trim();
                    }

                    // Clean the tag out of the text that gets spoken / displayed / saved to conversation history
                    cleanSpoken = Regex.Replace(spoken, @"\[SEND_PHOTO:[^\]]*\]", "", RegexOptions.IgnoreCase).Trim();

                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        _ = Task.Run(() => GenerateLorelaiSelfieAsync(scene));
                    }
                }

                // Ensure log and last_thinking are clean
                last_thinking = thoughts;

                // Add clean spoken response (thoughts kept app-side only for the "t" view)
                conversation.Add(new { role = "assistant", content = cleanSpoken });

                if (!string.IsNullOrEmpty(last_thinking))
                {
                    Console.WriteLine("[ t for thoughts ]");
                }

                var logEntry = new 
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    user = input,
                    lorelai = cleanSpoken,
                    llm = $"ollama:{activeModel}",
                    persona = "lorelai-genesis-v1.0"
                };
                File.AppendAllText(sessionFile, System.Text.Json.JsonSerializer.Serialize(logEntry) + Environment.NewLine);

                // Future: here we will hand off fullResponse.ToString() to TTS (Ara/Google/local)
                // Then after TTS completes, trigger image gen if [SEND_PHOTO] or scene detected.
                // For now: text is ready for audio.
            }

            Console.WriteLine("Grave-Lorelai text core shut down.");
        }

        static bool IsOllamaRunning()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = client.GetAsync("http://127.0.0.1:11434/api/tags").Result;
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // === Lorelai TTS Audio & Streaming Queue Configuration ===
        static BlockingCollection<string> ttsQueue = new BlockingCollection<string>();
        static CancellationTokenSource ttsCts = new CancellationTokenSource();
        static Dictionary<string, string> EnvVars = new Dictionary<string, string>();

        static void LoadEnv()
        {
            try
            {
                string envPath = Path.Combine(GetLorelaiRoot(), ".env");
                if (!File.Exists(envPath)) return;
                foreach (var line in File.ReadAllLines(envPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                    int idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        string key = line.Substring(0, idx).Trim();
                        string val = line.Substring(idx + 1).Trim();
                        if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                            val = val.Substring(1, val.Length - 2);
                        else if (val.StartsWith("'") && val.EndsWith("'") && val.Length >= 2)
                            val = val.Substring(1, val.Length - 2);
                        EnvVars[key] = val;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load .env file: {ex.Message}");
            }
        }

        static string GetEnv(string key, string defaultValue = "")
        {
            if (EnvVars.TryGetValue(key, out var val)) return val;
            var envVal = Environment.GetEnvironmentVariable(key);
            return !string.IsNullOrEmpty(envVal) ? envVal : defaultValue;
        }

        static async Task EnsureTtsServerRunningAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetAsync("http://127.0.0.1:8190/tts?text=test");
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    return; // Already running
                }
            }
            catch { }

            Console.WriteLine("TTS Server not detected. Launching local TTS Server...");
            try
            {
                string scriptPath = Path.Combine(GetLorelaiRoot(), "personal", "tts_server.py");
                if (File.Exists(scriptPath))
                {
                    var ttsInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"-u \"{scriptPath}\"",
                        WorkingDirectory = Path.Combine(GetLorelaiRoot(), "personal"),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };
                    Process.Start(ttsInfo);

                    // Wait for TTS server to be responsive
                    Console.Write("Waiting for TTS Server to start");
                    for (int i = 0; i < 30; i++)
                    {
                        Console.Write(".");
                        await Task.Delay(1000);
                        try
                        {
                            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                            var response = await client.GetAsync("http://127.0.0.1:8190/tts?text=test");
                            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                            {
                                Console.WriteLine(" ready.");
                                return;
                            }
                        }
                        catch { }
                    }
                    Console.WriteLine(" timeout. Continuing anyway, but audio might fail.");
                }
                else
                {
                    Console.WriteLine($"[WARNING] Local TTS script not found at: {scriptPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to auto-start local TTS: {ex.Message}");
            }
        }

        static async Task AudioWorker(CancellationToken ct)
        {
            var audioClient = new HttpClient();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ttsQueue.TryTake(out string? sentence, 100, ct))
                    {
                        if (string.IsNullOrWhiteSpace(sentence)) continue;
                        
                        string cleanText = Regex.Replace(sentence, @"\[SEND_PHOTO:[^\]]*\]", "", RegexOptions.IgnoreCase);
                        cleanText = Regex.Replace(cleanText, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        cleanText = cleanText.Replace("*", "").Replace("_", "").Trim();
                        if (string.IsNullOrWhiteSpace(cleanText)) continue;

                        byte[]? audioBytes = null;
                        string grokKey = GetEnv("GROK_TTS_API_KEY");

                        if (!string.IsNullOrEmpty(grokKey))
                        {
                            try
                            {
                                var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/tts");
                                requestMessage.Headers.Add("Authorization", $"Bearer {grokKey}");
                                
                                var payload = new
                                {
                                    text = cleanText,
                                    voice_id = "ara",
                                    language = "en",
                                    output_format = new { codec = "wav", sample_rate = 24000 }
                                };
                                string jsonStr = JsonSerializer.Serialize(payload);
                                requestMessage.Content = new StringContent(jsonStr, Encoding.UTF8, "application/json");
                                
                                var response = await audioClient.SendAsync(requestMessage, ct);
                                if (response.IsSuccessStatusCode)
                                {
                                    audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
                                }
                                else
                                {
                                    string errBody = await response.Content.ReadAsStringAsync(ct);
                                    Console.WriteLine($"\n(xAI TTS error: {response.StatusCode} - {errBody}. Falling back to local TTS...)");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\n(xAI TTS Connection failed: {ex.Message}. Falling back to local TTS...)");
                            }
                        }

                        if (audioBytes == null)
                        {
                            try
                            {
                                string encoded = Uri.EscapeDataString(cleanText);
                                string url = $"http://127.0.0.1:8190/tts?text={encoded}";
                                audioBytes = await audioClient.GetByteArrayAsync(url, ct);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\n(Local TTS playback error: {ex.Message})");
                            }
                        }

                        if (audioBytes != null && audioBytes.Length > 0)
                        {
                            try
                            {
                                using var ms = new MemoryStream(audioBytes);
                                using var player = new SoundPlayer(ms);
                                player.PlaySync();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\n(Audio playback hardware error: {ex.Message})");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n(AudioWorker exception: {ex.Message})");
                }
            }
        }

        static void ProcessSentenceBuffer(ref StringBuilder sentenceBuffer)
        {
            string content = sentenceBuffer.ToString();
            var matches = Regex.Matches(content, @"[\.\!\?\n](?:\s|$)");
            if (matches.Count > 0)
            {
                int lastIndex = 0;
                for (int i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    int endPos = match.Index + match.Length;
                    string sentence = content.Substring(lastIndex, endPos - lastIndex).Trim();
                    if (!string.IsNullOrEmpty(sentence))
                    {
                        ttsQueue.Add(sentence);
                    }
                    lastIndex = endPos;
                }
                
                string remainder = content.Substring(lastIndex);
                sentenceBuffer.Clear();
                sentenceBuffer.Append(remainder);
            }
        }

        static async Task<(string modelName, bool enableThinking)> SelectModelPromptAsync()
        {
            string defaultModel = GetEnv("OLLAMA_MODEL", "revenant/lorelai:latest");
            bool defaultThinking = GetEnv("OLLAMA_THINK", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await client.GetAsync("http://127.0.0.1:11434/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    string jsonStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonStr);
                    var modelsArray = doc.RootElement.GetProperty("models");
                    
                    var allModels = new List<(string name, bool thinking)>();
                    var candidateModels = new List<(string name, bool thinking)>();
                    
                    foreach (var m in modelsArray.EnumerateArray())
                    {
                        string name = m.GetProperty("name").GetString() ?? "";
                        bool thinking = false;
                        if (m.TryGetProperty("details", out var details) && details.TryGetProperty("capabilities", out var caps))
                        {
                            foreach (var cap in caps.EnumerateArray())
                            {
                                if (cap.GetString() == "thinking")
                                {
                                    thinking = true;
                                    break;
                                }
                            }
                        }
                        if (name.Contains("deepseek-r1") || name.Contains("thinking") || name.Contains("-9b"))
                        {
                            thinking = true;
                        }
                        
                        allModels.Add((name, thinking));
                        
                        bool matchesBrand = name.StartsWith("revenant/", StringComparison.OrdinalIgnoreCase) ||
                                            name.StartsWith("grave/", StringComparison.OrdinalIgnoreCase) ||
                                            name.StartsWith("from-the-grave/", StringComparison.OrdinalIgnoreCase) ||
                                            name.StartsWith("ftg/", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("lorelai", StringComparison.OrdinalIgnoreCase);
                        if (matchesBrand)
                        {
                            candidateModels.Add((name, thinking));
                        }
                    }

                    if (candidateModels.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("\nSelect Model for this session:");
                        for (int i = 0; i < candidateModels.Count; i++)
                        {
                            string isThinkingLabel = candidateModels[i].thinking ? " (thinking trace)" : "";
                            Console.WriteLine($"  [{i + 1}] {candidateModels[i].name}{isThinkingLabel}");
                        }
                        int otherIndex = candidateModels.Count + 1;
                        Console.WriteLine($"  [{otherIndex}] [Other] (Show all other installed models on this system)");
                        
                        int defaultIdx = candidateModels.FindIndex(m => m.name.Equals(defaultModel, StringComparison.OrdinalIgnoreCase));
                        if (defaultIdx < 0) defaultIdx = candidateModels.FindIndex(m => m.name.Contains("8b"));
                        if (defaultIdx < 0) defaultIdx = 0;

                        Console.Write($"Choose model (1-{otherIndex}, default [{defaultIdx + 1}] {candidateModels[defaultIdx].name}): ");
                        Console.ResetColor();

                        string? choice = Console.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(choice))
                        {
                            var selected = candidateModels[defaultIdx];
                            Console.WriteLine($"Using model: {selected.name} (thinking={selected.thinking})\n");
                            return (selected.name, selected.thinking);
                        }

                        if (int.TryParse(choice, out int idx))
                        {
                            if (idx == otherIndex)
                            {
                                return await PromptFromAllModelsAsync(allModels);
                            }
                            if (idx >= 1 && idx <= candidateModels.Count)
                            {
                                var selected = candidateModels[idx - 1];
                                Console.WriteLine($"Using model: {selected.name} (thinking={selected.thinking})\n");
                                return (selected.name, selected.thinking);
                            }
                        }
                    }
                    else if (allModels.Count > 0)
                    {
                        return await PromptFromAllModelsAsync(allModels);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n(Warning: Could not fetch dynamic models list: {ex.Message}. Using default.)");
            }

            return (defaultModel, defaultThinking);
        }

        static async Task<(string name, bool thinking)> PromptFromAllModelsAsync(List<(string name, bool thinking)> allModels)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nAll local Ollama models:");
            for (int i = 0; i < allModels.Count; i++)
            {
                string isThinkingLabel = allModels[i].thinking ? " (thinking trace)" : "";
                Console.WriteLine($"  [{i + 1}] {allModels[i].name}{isThinkingLabel}");
            }
            Console.Write($"Choose model (1-{allModels.Count}, default 1): ");
            Console.ResetColor();

            string? choice = Console.ReadLine()?.Trim();
            int selectedIdx = 0;
            if (!string.IsNullOrEmpty(choice) && int.TryParse(choice, out int idx) && idx >= 1 && idx <= allModels.Count)
            {
                selectedIdx = idx - 1;
            }

            var selected = allModels[selectedIdx];
            Console.WriteLine($"Using model: {selected.name} (thinking={selected.thinking})\n");
            return selected;
        }

        static async Task<(bool switched, string modelName, bool thinking)> HandleModelCommandAsync(string input, HttpClient httpClient)
        {
            try
            {
                var response = await httpClient.GetAsync("http://127.0.0.1:11434/api/tags");
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("(Error: Could not fetch local Ollama models list.)");
                    return (false, "", false);
                }

                string jsonStr = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonStr);
                var modelsArray = doc.RootElement.GetProperty("models");
                
                var allModels = new List<(string name, bool thinking)>();
                foreach (var m in modelsArray.EnumerateArray())
                {
                    string name = m.GetProperty("name").GetString() ?? "";
                    bool thinking = false;
                    if (m.TryGetProperty("details", out var details) && details.TryGetProperty("capabilities", out var caps))
                    {
                        foreach (var cap in caps.EnumerateArray())
                        {
                            if (cap.GetString() == "thinking")
                            {
                                thinking = true;
                                break;
                            }
                        }
                    }
                    if (name.Contains("deepseek-r1") || name.Contains("thinking") || name.Contains("-9b"))
                    {
                        thinking = true;
                    }
                    allModels.Add((name, thinking));
                }

                if (allModels.Count == 0)
                {
                    Console.WriteLine("(No local Ollama models found.)");
                    return (false, "", false);
                }

                string suffix = input.Substring(6).Trim();
                if (!string.IsNullOrEmpty(suffix))
                {
                    var match = allModels.FirstOrDefault(m => m.name.Contains(suffix, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(match.name))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Switched to model: {match.name} (thinking={match.thinking})");
                        Console.ResetColor();
                        return (true, match.name, match.thinking);
                    }
                    else
                    {
                        Console.WriteLine($"(Model '{suffix}' not found in local Ollama list.)");
                    }
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nAvailable local Ollama models:");
                for (int i = 0; i < allModels.Count; i++)
                {
                    string isThinkingLabel = allModels[i].thinking ? " (thinking trace)" : "";
                    Console.WriteLine($"  [{i + 1}] {allModels[i].name}{isThinkingLabel}");
                }
                Console.Write($"Select model number (1-{allModels.Count}) or type name to switch: ");
                Console.ResetColor();

                string? choice = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(choice))
                {
                    Console.WriteLine("(Model switch cancelled.)");
                    return (false, "", false);
                }

                if (int.TryParse(choice, out int selectedIdx) && selectedIdx >= 1 && selectedIdx <= allModels.Count)
                {
                    var selected = allModels[selectedIdx - 1];
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Switched to model: {selected.name} (thinking={selected.thinking})");
                    Console.ResetColor();
                    return (true, selected.name, selected.thinking);
                }
                else
                {
                    var match = allModels.FirstOrDefault(m => m.name.Contains(choice, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(match.name))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Switched to model: {match.name} (thinking={match.thinking})");
                        Console.ResetColor();
                        return (true, match.name, match.thinking);
                    }
                }

                Console.WriteLine("(Invalid selection. Model switch cancelled.)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(Error switching model: {ex.Message})");
            }

            return (false, "", false);
        }

        // === Lorelai Visual Customization, Camera, & ComfyUI integration ===
        static readonly string PonyQuality = "score_9, score_8_up, score_7_up, score_6_up, source_photo, ";
        static readonly string PhotoTag = "[SEND_PHOTO:";
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        static readonly string ComfyUrl = "http://127.0.0.1:8188";

        class VisualSettings
        {
            public string Checkpoint { get; set; } = "cyberrealisticPony_v180Coreshift.safetensors";
            public List<LoRAEntry> Loras { get; set; } = new List<LoRAEntry>
            {
                new LoRAEntry { Name = "cyberpunk_edgerunners_offset.safetensors", Strength = 0.7 },
                new LoRAEntry { Name = "cyberpunk_style_0006_anima.safetensors", Strength = 0.65 },
                new LoRAEntry { Name = "CyberwareByMakeThemComeAlive.safetensors", Strength = 0.6 }
            };
            public string Appearance { get; set; } = "slender 5'1\" hebe nubile young woman, vibrant fiery red hair in big loose waves, striking bright blue eyes with gold flecks and dark blue ring, pale pink skin full of life with light red and brown freckles on nose, cheeks, shoulders and chest, oval face with small pointed chin, full light pink lips, subtle biomechanical cyberpunk gold-brass metallic plating and circuitry seams on the left side of her face, neck and shoulder, high detail skin texture, cinematic lighting, photorealistic";
        }

        public class LoRAEntry
        {
            public string Name { get; set; }
            public double Strength { get; set; } = 0.8;
        }

        static VisualSettings CurrentVisual = LoadVisualSettings();

        static string GetLorelaiRoot()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."));
        }

        static VisualSettings LoadVisualSettings()
        {
            string path = Path.Combine(GetLorelaiRoot(), "lorelai_visual_settings.json");
            if (File.Exists(path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<VisualSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
                catch { }
            }
            var def = new VisualSettings();
            SaveVisualSettings(def);
            return def;
        }

        static void SaveVisualSettings(VisualSettings? settings = null)
        {
            if (settings == null) settings = CurrentVisual;
            string path = Path.Combine(GetLorelaiRoot(), "lorelai_visual_settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        static List<string> GetAvailableModels(string subfolder) // "checkpoints" or "loras"
        {
            string dir = Path.Combine(GetLorelaiRoot(), "img_gen", "ComfyUI", "models", subfolder);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.safetensors")
                            .Select(Path.GetFileName)
                            .Where(f => f != null)
                            .Select(f => f!)
                            .OrderBy(f => f)
                            .ToList();
        }

        static bool HandleVisualCommand(string input)
        {
            string lower = input.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lower)) return false;

            if (lower == "/looks" || lower == "/style" || lower == "/settings")
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Current visual settings for Lorelai:");
                Console.WriteLine($"  Checkpoint: {CurrentVisual.Checkpoint}");
                string loraList = CurrentVisual.Loras != null && CurrentVisual.Loras.Count > 0 
                    ? string.Join(" + ", CurrentVisual.Loras.Select(l => $"{l.Name} (x{l.Strength})")) 
                    : "none";
                Console.WriteLine($"  LoRAs: [{loraList}]");
                Console.WriteLine($"  Appearance (first 80 chars): {CurrentVisual.Appearance.Substring(0, Math.Min(80, CurrentVisual.Appearance.Length))}...");
                Console.ResetColor();

                var cps = GetAvailableModels("checkpoints");
                var lrs = GetAvailableModels("loras");
                Console.WriteLine($"Available checkpoints ({cps.Count}): {string.Join(", ", cps)}");
                Console.WriteLine($"Available LoRAs ({lrs.Count}): {string.Join(", ", lrs)}");
                Console.WriteLine("Use /lora <name or none>, /checkpoint <name>, /strength <0-2>, /appearance \"full new desc\" to change.");
                return true;
            }

            if (lower.StartsWith("/lora "))
            {
                string arg = input.Substring(6).Trim();
                if (arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentVisual.Loras.Clear();
                }
                else
                {
                    var available = GetAvailableModels("loras");
                    var match = available.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase) || m.Replace(".safetensors", "", StringComparison.OrdinalIgnoreCase).Contains(arg, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Console.WriteLine($"LoRA not found. Available: {string.Join(", ", available)}");
                        return true;
                    }
                    CurrentVisual.Loras.Clear();
                    CurrentVisual.Loras.Add(new LoRAEntry { Name = match, Strength = 0.8 });
                }
                SaveVisualSettings();
                string loraInfo = CurrentVisual.Loras.Count > 0 
                    ? string.Join(" + ", CurrentVisual.Loras.Select(l => $"{l.Name} (x{l.Strength})")) 
                    : "none";
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Lorelai's active LoRAs switched to: [{loraInfo}]. Next photo will use them.");
                Console.ResetColor();
                return true;
            }

            if (lower.StartsWith("/checkpoint "))
            {
                string arg = input.Substring(12).Trim();
                var available = GetAvailableModels("checkpoints");
                var match = available.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase) || m.Replace(".safetensors", "", StringComparison.OrdinalIgnoreCase).Contains(arg, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"Checkpoint not found. Available: {string.Join(", ", available)}");
                    return true;
                }
                CurrentVisual.Checkpoint = match;
                SaveVisualSettings();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Lorelai's base model switched to: {CurrentVisual.Checkpoint}. Next photo will use it.");
                Console.ResetColor();
                return true;
            }

            if (lower.StartsWith("/strength "))
            {
                if (double.TryParse(input.Substring(10).Trim(), out double s))
                {
                    double clamped = Math.Clamp(s, 0.0, 2.0);
                    if (CurrentVisual.Loras != null && CurrentVisual.Loras.Count > 0)
                    {
                        foreach (var l in CurrentVisual.Loras) l.Strength = clamped;
                    }
                    SaveVisualSettings();
                    string info = CurrentVisual.Loras != null && CurrentVisual.Loras.Count > 0 
                        ? string.Join(", ", CurrentVisual.Loras.Select(l => $"{l.Name}:x{l.Strength}")) 
                        : "none";
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"LoRA strength set to {clamped} for current LoRAs: [{info}].");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Usage: /strength 0.75   (applies to all active LoRAs)");
                }
                return true;
            }

            if (lower.StartsWith("/appearance "))
            {
                string newApp = input.Substring(12).Trim();
                if (!string.IsNullOrWhiteSpace(newApp))
                {
                    CurrentVisual.Appearance = newApp;
                    SaveVisualSettings();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Lorelai's base appearance for photos updated. Future [SEND_PHOTO] generations will use the new description. This is your full control over how she looks visually.");
                    Console.ResetColor();
                }
                return true;
            }

            if (lower == "/help" || lower == "/custom" || lower == "/customize")
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Lorelai Customization Commands:");
                Console.WriteLine("  /looks or /style          - show current + list all available image models/LoRAs");
                Console.WriteLine("  /lora <partial name or none>   - change image LoRA");
                Console.WriteLine("  /checkpoint <partial name>     - change base image checkpoint");
                Console.WriteLine("  /strength 0.8                  - LoRA intensity (0.0 = off, 1.0 = full)");
                Console.WriteLine("  /appearance \"new description...\" - change her visual description");
                Console.WriteLine("  /model                         - list and swap between installed local Ollama LLM text models");
                Console.WriteLine("  /photo <description>           - force a test photo generation");
                Console.WriteLine("Changes are instant and saved. This gives you direct control over how she looks and acts.");
                Console.ResetColor();
                return true;
            }

            return false;
        }

        static async Task GenerateLorelaiSelfieAsync(string sceneDescription)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n[Lorelai is taking a photo...]");
                Console.ResetColor();

                string positive = PonyQuality + CurrentVisual.Appearance + ", " + sceneDescription + ", beautiful detailed face, sharp focus, natural skin pores, soft lighting";
                string negative = "score_4, score_5, score_6, deformed, bad anatomy, extra limbs, blurry, lowres, text, watermark, signature, censored, ugly, poorly drawn face, mutation, extra fingers";
                int seed = Random.Shared.Next(0, int.MaxValue);

                // Build the SDXL workflow dynamically from current user settings.
                var workflow = new JsonObject
                {
                    ["1"] = new JsonObject
                    {
                        ["inputs"] = new JsonObject { ["ckpt_name"] = CurrentVisual.Checkpoint },
                        ["class_type"] = "CheckpointLoaderSimple"
                    }
                };

                string modelSource = "1";
                string clipSource = "1";

                if (CurrentVisual.Loras != null && CurrentVisual.Loras.Count > 0)
                {
                    int loraNodeId = 9;
                    string currentModel = "1";
                    string currentClip = "1";
                    foreach (var loraEntry in CurrentVisual.Loras)
                    {
                        workflow[loraNodeId.ToString()] = new JsonObject
                        {
                            ["inputs"] = new JsonObject
                            {
                                ["lora_name"] = loraEntry.Name,
                                ["strength_model"] = loraEntry.Strength,
                                ["strength_clip"] = loraEntry.Strength,
                                ["model"] = new JsonArray(currentModel, 0),
                                ["clip"] = new JsonArray(currentClip, 1)
                            },
                            ["class_type"] = "LoraLoader"
                        };
                        currentModel = loraNodeId.ToString();
                        currentClip = loraNodeId.ToString();
                        loraNodeId++;
                    }
                    modelSource = currentModel;
                    clipSource = currentClip;
                }

                workflow["4"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["text"] = positive,
                        ["clip"] = new JsonArray(clipSource, 1)
                    },
                    ["class_type"] = "CLIPTextEncode"
                };

                workflow["5"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["text"] = negative,
                        ["clip"] = new JsonArray(clipSource, 1)
                    },
                    ["class_type"] = "CLIPTextEncode"
                };

                workflow["3"] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["width"] = 512, ["height"] = 768, ["batch_size"] = 1 },
                    ["class_type"] = "EmptyLatentImage"
                };

                workflow["6"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["seed"] = seed,
                        ["steps"] = 20,
                        ["cfg"] = 4,
                        ["sampler_name"] = "euler",
                        ["scheduler"] = "normal",
                        ["denoise"] = 1,
                        ["model"] = new JsonArray(modelSource, 0),
                        ["positive"] = new JsonArray("4", 0),
                        ["negative"] = new JsonArray("5", 0),
                        ["latent_image"] = new JsonArray("3", 0)
                    },
                    ["class_type"] = "KSampler"
                };

                workflow["7"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["samples"] = new JsonArray("6", 0),
                        ["vae"] = new JsonArray("1", 2)
                    },
                    ["class_type"] = "VAEDecode"
                };

                workflow["8"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["filename_prefix"] = "Lorelai",
                        ["images"] = new JsonArray("7", 0)
                    },
                    ["class_type"] = "SaveImage"
                };

                var promptPayload = new { prompt = workflow };

                await EnsureComfyRunningAsync();

                string payloadJson = JsonSerializer.Serialize(promptPayload);
                var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var submitResp = await http.PostAsync($"{ComfyUrl}/prompt", content);
                string submitStr = await submitResp.Content.ReadAsStringAsync();

                if (!submitResp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Comfy submit error] Status: {submitResp.StatusCode} Body: {submitStr}");
                    throw new Exception("ComfyUI prompt submit failed");
                }

                JsonNode submitJson = JsonNode.Parse(submitStr)!;
                if (submitJson["prompt_id"] == null)
                {
                    throw new Exception("No prompt_id in Comfy response");
                }
                string promptId = submitJson["prompt_id"]!.GetValue<string>();

                string? imageFilename = null;
                for (int i = 0; i < 300; i++)
                {
                    await Task.Delay(1000);
                    var histResp = await http.GetAsync($"{ComfyUrl}/history/{promptId}");
                    string histStr = await histResp.Content.ReadAsStringAsync();

                    if (!histResp.IsSuccessStatusCode) continue;

                    JsonNode hist = JsonNode.Parse(histStr)!;
                    if (hist != null && hist[promptId] != null)
                    {
                        var node = hist[promptId]!;
                        var status = node["status"];
                        var outputs = node["outputs"];

                        bool done = (status != null && (status["completed"]?.GetValue<bool>() == true || status["status_str"]?.GetValue<string>() == "success"))
                                    || (outputs != null && outputs["8"] != null);

                        if (done)
                        {
                            if (outputs != null && outputs["8"] != null)
                            {
                                var images = outputs["8"]!["images"];
                                if (images != null && images.AsArray().Count > 0)
                                {
                                    imageFilename = images[0]!["filename"]!.GetValue<string>();
                                    Console.WriteLine($"[Comfy] Got image filename: {imageFilename}");
                                }
                            }
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(imageFilename))
                {
                    Console.WriteLine("[Lorelai] Couldn't finish the photo (timeout or error).");
                    return;
                }

                string comfyOutput = Path.Combine(GetLorelaiRoot(), "img_gen", "ComfyUI", "output", imageFilename);
                string selfiesDir = Path.Combine(GetLorelaiRoot(), "selfies");
                Directory.CreateDirectory(selfiesDir);

                string safeScene = Regex.Replace(sceneDescription, @"[^a-zA-Z0-9_-]", "_").ToLowerInvariant();
                if (safeScene.Length > 60) safeScene = safeScene.Substring(0, 60);
                string destName = $"lorelai_{DateTime.Now:yyyyMMdd_HHmmss}_{safeScene}.png";
                string destPath = Path.Combine(selfiesDir, destName);

                if (File.Exists(comfyOutput))
                {
                    File.Copy(comfyOutput, destPath, true);
                }
                else
                {
                    var viewUrl = $"{ComfyUrl}/view?filename={Uri.EscapeDataString(imageFilename)}&type=output";
                    var imgBytes = await http.GetByteArrayAsync(viewUrl);
                    File.WriteAllBytes(destPath, imgBytes);
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[Lorelai sent a photo - saved to: {destName}. Open it from the selfies folder.]");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera Error]: {ex.Message}");
            }
        }

        static async Task EnsureComfyRunningAsync()
        {
            try
            {
                var probe = await http.GetAsync($"{ComfyUrl}/system_stats");
                if (probe.IsSuccessStatusCode)
                {
                    return; // Already running
                }
            }
            catch { }

            Console.WriteLine("ComfyUI not detected. Launching ComfyUI in the background...");
            try
            {
                string imgGenDir = Path.Combine(GetLorelaiRoot(), "img_gen");
                string batPath = Path.Combine(imgGenDir, "run_nvidia_gpu.bat");

                if (File.Exists(batPath))
                {
                    var comfyInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{batPath}\"",
                        WorkingDirectory = imgGenDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    Process.Start(comfyInfo);

                    // Wait for ComfyUI to be responsive
                    Console.Write("Waiting for ComfyUI to start");
                    for (int i = 0; i < 30; i++)
                    {
                        Console.Write(".");
                        await Task.Delay(1000);
                        try
                        {
                            var probe = await http.GetAsync($"{ComfyUrl}/system_stats");
                            if (probe.IsSuccessStatusCode)
                            {
                                Console.WriteLine(" ready.");
                                return;
                            }
                        }
                        catch { }
                    }
                    Console.WriteLine(" timeout. Continuing anyway, but photo feature might fail.");
                }
                else
                {
                    Console.WriteLine($"[WARNING] ComfyUI batch file not found at: {batPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to auto-start ComfyUI: {ex.Message}");
            }
        }
    }
}
