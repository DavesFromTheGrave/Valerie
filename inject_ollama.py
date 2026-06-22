import sys

with open(r'm:\Projects\Valerie\Program.cs', 'r', encoding='utf-8') as f:
    text = f.read()

stream_ollama_func = """
        static async Task StreamOllamaChatAsync(HttpClient httpClient, string activeModel, List<object> conversation, StringBuilder fullResponse, StringBuilder thoughtsBuffer, StringBuilder sentenceBuffer, ref bool isThinking, ref bool thinkingShown)
        {
            char[] spinner = new char[] { '|', '/', '-', '\\\\' };
            int spinnerIndex = 0;

            var requestBody = new 
            {
                model = activeModel,
                messages = conversation.ToArray(),
                stream = true,
                think = enableThinking
            };

            var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("http://127.0.0.1:11434/api/chat", content);
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
                            
                            // Show spinner
                            if (!thinkingShown)
                            {
                                Console.Write("Thinking...  ");
                                thinkingShown = true;
                            }
                            Console.Write($"\\b{spinner[spinnerIndex]}");
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
                                Console.Write("\\b \\b");
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
                                thoughtsBuffer.Append(temp.Substring(0, endIdx));
                                temp = temp.Substring(endIdx + 8);
                            }

                            if (isThinking)
                            {
                                thoughtsBuffer.Append(temp);
                                continue; // Don't print thoughts to the text console
                            }

                            // Native spoken content
                            Console.Write(temp);
                            fullResponse.Append(temp);
                            sentenceBuffer.Append(temp);

                            // Trigger TTS dynamically on punctuation
                            string currentSentence = sentenceBuffer.ToString();
                            if (currentSentence.Contains(".") || currentSentence.Contains("!") || currentSentence.Contains("?"))
                            {
                                // Find last punctuation mark
                                int lastPunc = -1;
                                int dotIdx = currentSentence.LastIndexOf(".");
                                int exclIdx = currentSentence.LastIndexOf("!");
                                int qIdx = currentSentence.LastIndexOf("?");
                                lastPunc = Math.Max(dotIdx, Math.Max(exclIdx, qIdx));

                                if (lastPunc != -1)
                                {
                                    string toSpeak = currentSentence.Substring(0, lastPunc + 1).Trim();
                                    string remainder = currentSentence.Substring(lastPunc + 1);
                                    
                                    if (!string.IsNullOrWhiteSpace(toSpeak))
                                    {
                                        ttsQueue.Enqueue(toSpeak);
                                    }
                                    
                                    sentenceBuffer.Clear();
                                    sentenceBuffer.Append(remainder);
                                }
                            }
                        }
                    }
                }
            }

            // Flush remaining text to TTS
            string finalSentence = sentenceBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(finalSentence))
            {
                ttsQueue.Enqueue(finalSentence);
            }
        }
"""

new_text = text.replace('        static async Task StreamZenithChatAsync', stream_ollama_func + '\n        static async Task StreamZenithChatAsync')

with open(r'm:\Projects\Valerie\Program.cs', 'w', encoding='utf-8') as f:
    f.write(new_text)

print("Injected StreamOllamaChatAsync successfully!")
