import re

with open(r'm:\Projects\Valerie\Program.cs', 'r', encoding='utf-8') as f:
    text = f.read()

# The fuzzy replace injected the whole file contents from using System.Threading.Tasks down to the end of HandleUserTurnAsync
# Let's find where the duplication starts
match = re.search(r'(Console\.WriteLine\("GPU handoff note: LLM on GPU now; later queue ComfyUI to avoid VRAM fight on 3070 Ti\.\\n"\);\s+if \(isFirstBoot\)\s+\{\s+Console\.WriteLine\("First boot complete\. On next launch Valerie will greet you by name through the speakers\."\);\s+\}\s+)(using System\.Threading\.Tasks;.*?)(\s+static async Task StreamZenithChatAsync)', text, re.DOTALL)

if match:
    before = text[:match.start(2)]
    after = match.group(3) + text[match.end(3):]
    
    # We need to construct the correct Main end and HandleUserTurnAsync
    correct_injection = """
            // Auto-logging for recording system
            string logDir = Path.Combine("personal", "chats", "valerie");
            Directory.CreateDirectory(logDir);
            sessionFile = Path.Combine(logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            last_thinking = "";
            conversation = new List<object>();
            httpClient = new HttpClient();

            // Start WinForms Application Context
            Application.Run(new ValerieApplicationContext(HandleUserTurnAsync));
        }

        public static async Task HandleUserTurnAsync(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Application.Exit();
                return;
            }

            // Intercept visual customization commands first
            if (HandleVisualCommand(input)) return;

            // Intercept LLM model switching command
            if (input.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
            {
                var result = await HandleModelCommandAsync(input, httpClient);
                if (result.switched)
                {
                    activeModel = result.modelName;
                    enableThinking = result.thinking;
                }
                return;
            }

            // Quick force test for photo generation
            if (input.Trim().ToLower().StartsWith("/photo "))
            {
                string scene = input.Trim().Substring(7).Trim();
                if (!string.IsNullOrWhiteSpace(scene))
                {
                    Console.WriteLine($"[Forcing test photo with scene: {scene}]");
                    _ = Task.Run(() => GenerateValerieSelfieAsync(scene));
                }
                else
                {
                    Console.WriteLine("Usage: /photo close-up selfie in my bedroom, wearing cyberpunk outfit, smiling at camera");
                }
                return;
            }

            if (input.Equals("t", StringComparison.OrdinalIgnoreCase) || input.Equals("thoughts", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(last_thinking))
                    Console.WriteLine("\\n--- V's thoughts ---\\n" + last_thinking + "\\n---\\n");
                else
                    Console.WriteLine("No thoughts for the last turn.");
                return;
            }

            // Add user message to conversation history for proper back-and-forth
            conversation.Add(new { role = "user", content = input });

            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.Write("Valerie (spoken text): ");
            Console.ResetColor();

            StringBuilder fullResponse = new StringBuilder();
            StringBuilder thoughtsBuffer = new StringBuilder();
            StringBuilder sentenceBuffer = new StringBuilder();
            
            bool isThinking = false;
            bool thinkingShown = false;

            if (activeBackend == LlmBackend.Zenith)
            {
                await StreamZenithChatAsync(httpClient, conversation, fullResponse, thoughtsBuffer, sentenceBuffer);
            }
            else
            {
                await StreamOllamaChatAsync(httpClient, activeModel, conversation, fullResponse, thoughtsBuffer, sentenceBuffer, ref isThinking, ref thinkingShown);
            }

            Console.WriteLine();
            string cleanSpoken = fullResponse.ToString().Trim();
            last_thinking = thoughtsBuffer.ToString().Trim();

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
                v = cleanSpoken,
                llm = $"ollama:{activeModel}",
                persona = "v-4.7.5-compressed (baked in model)"
            };
            File.AppendAllText(sessionFile, System.Text.Json.JsonSerializer.Serialize(logEntry) + Environment.NewLine);
        }
"""
    
    with open(r'm:\Projects\Valerie\Program.cs', 'w', encoding='utf-8') as f:
        f.write(before + correct_injection + after)
    print("Successfully patched Program.cs via script")
else:
    print("Could not find the duplicated block to replace!")
