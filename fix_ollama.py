import re

with open(r'm:\Projects\Valerie\Program_HEAD.cs', 'r', encoding='utf-16') as f:
    old_text = f.read()

# Extract StreamOllamaChatAsync
# It should be between the end of Main and StreamZenithChatAsync
match = re.search(r'(static async Task StreamOllamaChatAsync.*?)\s+static async Task StreamZenithChatAsync', old_text, re.DOTALL)

if match:
    ollama_func = match.group(1).strip() + "\n\n"
    
    with open(r'm:\Projects\Valerie\Program.cs', 'r', encoding='utf-8') as f:
        current_text = f.read()
        
    # Insert before StreamZenithChatAsync
    new_text = current_text.replace('static async Task StreamZenithChatAsync', ollama_func + '        static async Task StreamZenithChatAsync')
    
    with open(r'm:\Projects\Valerie\Program.cs', 'w', encoding='utf-8') as f:
        f.write(new_text)
        
    print("Successfully injected StreamOllamaChatAsync back into Program.cs")
else:
    print("Could not find StreamOllamaChatAsync in HEAD!")
