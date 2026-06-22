import re
import sys

with open("Program.cs", "r", encoding="utf-8") as f:
    code = f.read()

# 1. Add Enum
enum_code = """    class Program
    {
        enum LlmBackend { Ollama, Zenith }
        static LlmBackend activeBackend = LlmBackend.Ollama;"""
code = code.replace("    class Program\n    {", enum_code)

# 2. Add backend selection
backend_sel = """
            Console.WriteLine("\\nSelect LLM Backend for this session:");
            Console.WriteLine("  [1] Ollama (Local) - Default");
            Console.WriteLine("  [2] Zenith AI (Cloud) - openai/gpt-5.5");
            Console.Write("Choose backend (1-2, default 1): ");
            string? backendChoice = Console.ReadLine()?.Trim();
            if (backendChoice == "2")
            {
                activeBackend = LlmBackend.Zenith;
            }

            // Dynamic model selection based on local Ollama tags"""
code = code.replace("            // Dynamic model selection based on local Ollama tags", backend_sel)

# 3. Add StreamZenithChatAsync
stream_zenith = """
        static async Task StreamZenithChatAsync(HttpClient httpClient, List<object> conversation, StringBuilder fullResponse, StringBuilder thoughtsBuffer, StringBuilder sentenceBuffer)
        {
            string zenithKey = GetEnv("ZENITH_API_KEY");
            if (string.IsNullOrEmpty(zenithKey))
            {
                Console.WriteLine("\\n[ERROR] ZENITH_API_KEY not found in .env");
                return;
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.zenllm.org/v1/chat/completions");
            requestMessage.Headers.Add("Authorization", $"Bearer {zenithKey}");
            requestMessage.Headers.Add("User-Agent", "Valerie/1.0");

            var requestBody = new 
            {
                model = GetEnv("ZENITH_MODEL", "openai/gpt-5.5"),
                messages = conversation.ToArray(),
                stream = true
            };

            string jsonStr = System.Text.Json.JsonSerializer.Serialize(requestBody);
            requestMessage.Content = new StringContent(jsonStr, Encoding.UTF8, "application/json");

            try
            {
                using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"\\n[Zenith API Error] {response.StatusCode}: {errorBody}");
                    return; // DO NOT THROW, just return cleanly
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("data: [DONE]")) break;
                    if (line.StartsWith("data: "))
                    {
                        string dataStr = line.Substring(6);
                        try
                        {
                            var chunk = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataStr);
                            if (chunk.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                            {
                                var delta = choices[0].GetProperty("delta");
                                if (delta.TryGetProperty("content", out var contentElement))
                                {
                                    string? text = contentElement.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        Console.Write(text);
                                        fullResponse.Append(text);
                                        sentenceBuffer.Append(text);
                                        ProcessSentenceBuffer(ref sentenceBuffer);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\\n[Zenith Stream Error] {ex.Message}");
            }
        }

        static bool IsOllamaRunning()"""
code = code.replace("        static bool IsOllamaRunning()", stream_zenith)

# 4. Wrap LLM in try-catch and route to Zenith
ollama_call = """
                try
                {
                    if (activeBackend == LlmBackend.Ollama)
                    {
                        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();

                        using var stream = await response.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(stream);
                        string? line;
                        
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var chunk = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line);
                            
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
                                        if (!thinkingShown)
                                        {
                                            Console.Write("Thinking...  ");
                                            thinkingShown = true;
                                        }
                                        Console.Write($"\\b{spinner[spinnerIndex]}");
                                        spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                                        continue;
                                    }
                                }

                                // 2. Check spoken content chunk
                                if (msgElement.TryGetProperty("content", out var contentElement))
                                {
                                    string? text = contentElement.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        if (thinkingShown)
                                        {
                                            Console.Write("\\b \\b");
                                            thinkingShown = false;
                                        }

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
                                            if (!thinkingShown)
                                            {
                                                Console.Write("Thinking...  ");
                                                thinkingShown = true;
                                            }
                                            Console.Write($"\\b{spinner[spinnerIndex]}");
                                            spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                                        }
                                        else
                                        {
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
                    }
                    else if (activeBackend == LlmBackend.Zenith)
                    {
                        await StreamZenithChatAsync(httpClient, conversation, fullResponse, thoughtsBuffer, sentenceBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\\n[ERROR] Connection to LLM Backend failed: {ex.Message}");
                }"""
pattern = r"                using var response = await httpClient\.SendAsync.*?break;\s*}\s*}"
code = re.sub(pattern, ollama_call, code, flags=re.DOTALL)

# 5. Fix Zenith TTS Routing in AudioWorker
zenith_tts = """
                        byte[]? audioBytes = null;
                        string grokKey = GetEnv("GROK_TTS_API_KEY");
                        string zenithKey = GetEnv("ZENITH_API_KEY");

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
                                string jsonStr = System.Text.Json.JsonSerializer.Serialize(payload);
                                requestMessage.Content = new StringContent(jsonStr, Encoding.UTF8, "application/json");
                                
                                var response = await audioClient.SendAsync(requestMessage, ct);
                                if (response.IsSuccessStatusCode)
                                {
                                    audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
                                }
                                else
                                {
                                    string errBody = await response.Content.ReadAsStringAsync(ct);
                                    Console.WriteLine($"\\n(xAI TTS error: {response.StatusCode} - {errBody}. Falling back to local TTS...)");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\\n(xAI TTS Connection failed: {ex.Message}. Falling back to local TTS...)");
                            }
                        }
                        else if (!string.IsNullOrEmpty(GetEnv("ZENITH_TTS_MODEL")))
                        {
                            try
                            {
                                var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.zenllm.org/v1/audio/speech");
                                requestMessage.Headers.Add("Authorization", $"Bearer {zenithKey}");
                                requestMessage.Headers.Add("User-Agent", "Valerie/1.0");
                                
                                string ttsModel = GetEnv("ZENITH_TTS_MODEL", "xai/grok-tts-1");
                                string ttsVoice = GetEnv("ZENITH_TTS_VOICE", "ara");
                                
                                var payload = new
                                {
                                    input = cleanText,
                                    model = ttsModel,
                                    voice = ttsVoice,
                                    response_format = "wav"
                                };
                                string jsonStr = System.Text.Json.JsonSerializer.Serialize(payload);
                                requestMessage.Content = new StringContent(jsonStr, Encoding.UTF8, "application/json");
                                
                                var response = await audioClient.SendAsync(requestMessage, ct);
                                if (response.IsSuccessStatusCode)
                                {
                                    audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
                                }
                                else
                                {
                                    string errBody = await response.Content.ReadAsStringAsync(ct);
                                    Console.WriteLine($"\\n(Zenith TTS error: {response.StatusCode} - {errBody}. Falling back to local TTS...)");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\\n(Zenith TTS Connection failed: {ex.Message}. Falling back to local TTS...)");
                            }
                        }"""
pattern2 = r"                        byte\[\]\? audioBytes = null;.*?Falling back to local TTS\.\.\.\)\"\);\s*}\s*}"
code = re.sub(pattern2, zenith_tts, code, flags=re.DOTALL)

with open("Program.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("Patched!")
