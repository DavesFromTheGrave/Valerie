#!/usr/bin/env python
"""
Simple LoRA merger tool for ComfyUI / SDXL.

Merges multiple LoRA .safetensors files into one by weighted sum of their tensors.
This lets you combine several LoRAs (e.g. cyberpunk styles) into a single file
so you only need to load "one LoRA" in the workflow / bot config.

Usage: run with the project's embedded Python:
  ComfyUI\python_embeded\python.exe merge_loras.py

Output will be placed in the loras folder.
"""

import os
from safetensors.torch import load_file, save_file
import torch

# === CONFIGURE HERE ===
# List of (filename, strength) to merge. Strengths are relative weights.
# The merged file will be saved with strength=1.0 in the bot settings.
LORAS_TO_MERGE = [
    ("cyberpunk_edgerunners_offset.safetensors", 0.7),
    ("cyberpunk_style_0006_anima.safetensors", 0.65),
    ("CyberwareByMakeThemComeAlive.safetensors", 0.6),
]

OUTPUT_NAME = "cyberpunk_merged_lora.safetensors"

# Path relative to this script (assumes script is in Lorelai root)
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LORAS_DIR = os.path.join(BASE_DIR, "ComfyUI", "ComfyUI", "models", "loras")
OUTPUT_PATH = os.path.join(LORAS_DIR, OUTPUT_NAME)

def merge_loras(lora_list, output_path):
    merged_sd = {}
    total_weight = 0.0

    for filename, strength in lora_list:
        path = os.path.join(LORAS_DIR, filename)
        if not os.path.exists(path):
            print(f"WARNING: {path} not found, skipping.")
            continue
        print(f"Loading {filename} with strength {strength}")
        sd = load_file(path)
        total_weight += strength
        for key, tensor in sd.items():
            tensor = tensor.float()  # ensure float for merging
            if key in merged_sd:
                merged_sd[key] += tensor * strength
            else:
                merged_sd[key] = tensor * strength

    if not merged_sd:
        print("No LoRAs loaded, nothing to merge.")
        return

    # Optional: normalize if you want the total strength to feel like ~1.0
    # For most use cases people just use the raw weighted sum.
    # If you want to normalize:
    # for k in merged_sd:
    #     merged_sd[k] /= total_weight

    # Convert back to the original dtype if possible (usually bf16/fp16)
    # safetensors will handle it; we can leave as float32 or cast.
    # For safety we cast to float16 (common for LoRAs)
    for k in merged_sd:
        merged_sd[k] = merged_sd[k].to(torch.float16)

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    save_file(merged_sd, output_path)
    print(f"\nMerged LoRA saved to:\n{output_path}")
    print(f"Total merged weight: {total_weight:.2f}")

if __name__ == "__main__":
    print("=== LoRA Merger for Lorelai ===")
    merge_loras(LORAS_TO_MERGE, OUTPUT_PATH)
    print("Done. You can now set this merged file as the single LoRA in lorelai_visual_settings.json or via the /lora command.")