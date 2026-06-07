using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GraveLorelai
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing Grave-Lorelai (text generation core - audio native path)...");

            // Paths - using existing model from prior setup for square one start.
            // Later: move model into this folder or make configurable.
            string modelPath = @"M:\Projects\Lorelai\models\llm_model.gguf";
            string personaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "V_Persona.txt"); // relative for dev
            // Fallback to explicit if relative fails in release
            if (!File.Exists(personaPath))
            {
                personaPath = @"M:\Projects\Grave-Lorelai\V_Persona.txt";
            }

            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"[ERROR] Model not found at {modelPath}. Place your .gguf here or update path.");
                return;
            }

            string systemPrompt = File.ReadAllText(personaPath);

            Console.WriteLine("\nGrave-Lorelai text gen ready (V persona - spoken words first, via Ollama model revenant/nsfw-v-8b:latest with baked v4.5.5 prompt).");
            Console.WriteLine("Type message. Output is the text for audio/TTS. 'exit' to quit.\n");
            Console.WriteLine("Audio goal: local LLM here for content -> TTS (cloud Ara/Google or future local) -> local images after spoken line.");
            Console.WriteLine("GPU handoff note: LLM on GPU now; later queue ComfyUI to avoid VRAM fight on 3070 Ti.\n");

            // Auto-logging for recording system (starts proper engineering data collection, separated by LLM/persona)
            string logDir = Path.Combine("personal", "chats", "v");
            Directory.CreateDirectory(logDir);
            string sessionFile = Path.Combine(logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            var httpClient = new HttpClient();

            while (true)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("You: ");
                Console.ResetColor();

                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input) || input.Trim().ToLower() == "exit") break;

                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write("V (spoken text): ");
                Console.ResetColor();

                StringBuilder fullResponse = new StringBuilder();

                var requestBody = new 
                {
                    model = "revenant/nsfw-v-8b:latest",
                    messages = new[] { new { role = "user", content = input } },
                    stream = true
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync("http://localhost:11434/api/chat", content);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var chunk = JsonSerializer.Deserialize<JsonElement>(line);
                    if (chunk.TryGetProperty("message", out var msgElement) && msgElement.TryGetProperty("content", out var contentElement))
                    {
                        string text = contentElement.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            Console.Write(text);
                            fullResponse.Append(text);
                        }
                    }
                    if (chunk.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean())
                    {
                        break;
                    }
                }

                Console.WriteLine("\n");

                var logEntry = new 
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    user = input,
                    v = fullResponse.ToString().Trim(),
                    llm = "ollama:revenant/nsfw-v-8b:latest",
                    persona = "v-4.5.5-compressed (baked in model)"
                };
                File.AppendAllText(sessionFile, System.Text.Json.JsonSerializer.Serialize(logEntry) + Environment.NewLine);

                // Future: here we will hand off fullResponse.ToString() to TTS (Ara/Google/local)
                // Then after TTS completes, trigger image gen if [SEND_PHOTO] or scene detected.
                // For now: text is ready for audio.
            }

            Console.WriteLine("Grave-Lorelai text core shut down.");
        }
    }
}
