import urllib.request
import json
import time
import os

# Read visual settings
with open('lorelai_visual_settings.json', 'r') as f:
    settings = json.load(f)

checkpoint = settings.get("Checkpoint", "cyberrealisticPony_v180Coreshift.safetensors")
loras = settings.get("Loras", [])
appearance = settings.get("Appearance", "")

scene_description = "full body shot, wearing a highly revealing tiny black string bikini, showing beautiful bare skin and flawless anatomy, standing in a softly lit luxury bedroom"

pony_quality = "score_9, score_8_up, score_7_up, score_6_up, source_photo, "
positive = pony_quality + appearance + ", " + scene_description + ", beautiful detailed face, sharp focus, natural skin pores, soft lighting"
negative = "score_4, score_5, score_6, deformed, bad anatomy, extra limbs, blurry, lowres, text, watermark, signature, censored, ugly, poorly drawn face, mutation, extra fingers, sunglasses, glasses, straight bangs, blunt bangs, hispanic, latina, dark skin, tan, clothes, fully dressed"

workflow = {
    "1": {
        "inputs": { "ckpt_name": checkpoint },
        "class_type": "CheckpointLoaderSimple"
    }
}

model_source = "1"
clip_source = "1"

if loras:
    lora_id = 9
    for lora in loras:
        workflow[str(lora_id)] = {
            "inputs": {
                "lora_name": lora["Name"],
                "strength_model": lora.get("Strength", 0.8),
                "strength_clip": lora.get("Strength", 0.8),
                "model": [model_source, 0],
                "clip": [clip_source, 1]
            },
            "class_type": "LoraLoader"
        }
        model_source = str(lora_id)
        clip_source = str(lora_id)
        lora_id += 1

workflow["4"] = {
    "inputs": { "text": positive, "clip": [clip_source, 1] },
    "class_type": "CLIPTextEncode"
}

workflow["5"] = {
    "inputs": { "text": negative, "clip": [clip_source, 1] },
    "class_type": "CLIPTextEncode"
}

workflow["3"] = {
    "inputs": { "width": 832, "height": 1216, "batch_size": 1 },  # Adjusted for SDXL portrait
    "class_type": "EmptyLatentImage"
}

workflow["6"] = {
    "inputs": {
        "seed": int(time.time()),
        "steps": 25,
        "cfg": 5,
        "sampler_name": "euler",
        "scheduler": "normal",
        "denoise": 1,
        "model": [model_source, 0],
        "positive": ["4", 0],
        "negative": ["5", 0],
        "latent_image": ["3", 0]
    },
    "class_type": "KSampler"
}

workflow["7"] = {
    "inputs": { "samples": ["6", 0], "vae": ["1", 2] },
    "class_type": "VAEDecode"
}

workflow["8"] = {
    "inputs": { "filename_prefix": "Lorelai_Test", "images": ["7", 0] },
    "class_type": "SaveImage"
}

data = json.dumps({"prompt": workflow}).encode('utf-8')
req = urllib.request.Request("http://127.0.0.1:8188/prompt", data=data, headers={'Content-Type': 'application/json'})

print("Sending test request to ComfyUI...")
try:
    with urllib.request.urlopen(req) as response:
        res = json.loads(response.read())
        print(f"Success! Prompt ID: {res['prompt_id']}")
        print("Check your ComfyUI window to watch it generate. The image will be saved in M:\\Projects\\Lorelai\\ComfyUI\\ComfyUI\\output\\")
except Exception as e:
    print(f"Error communicating with ComfyUI. Is it running? {e}")
