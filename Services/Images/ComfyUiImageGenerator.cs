using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Valerie.Models;

namespace Valerie.Services.Images;

public sealed class ComfyUiImageGenerator : IImageGenerator
{
    private readonly ImageOptions _options;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task>? _releaseLlmVram;

    /// <param name="releaseLlmVram">Optional hook invoked before generation to free the LLM's GPU
    /// memory (the 8GB LLM↔SDXL handoff). Null = no handoff (e.g. plenty of VRAM).</param>
    public ComfyUiImageGenerator(ImageOptions options, HttpClient http,
        Func<CancellationToken, Task>? releaseLlmVram = null)
    {
        _options = options;
        _http = http;
        _releaseLlmVram = releaseLlmVram;
    }

    public async Task EnsureComfyRunningAsync()
    {
        try
        {
            var probe = await _http.GetAsync($"{_options.ComfyUrl}/system_stats");
            if (probe.IsSuccessStatusCode) return;
        }
        catch { }

        var bat = _options.LaunchBat;
        if (string.IsNullOrWhiteSpace(bat) || !File.Exists(bat))
        {
            Console.WriteLine("[ComfyUI] Not running and no LaunchBat configured — photos will fail.");
            return;
        }

        Console.WriteLine("[ComfyUI] Not detected. Launching in background...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                WorkingDirectory = Path.GetDirectoryName(bat)!,
                CreateNoWindow = true,
                UseShellExecute = false
            });

            Console.Write("[ComfyUI] Waiting for startup");
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                Console.Write(".");
                try
                {
                    var probe = await _http.GetAsync($"{_options.ComfyUrl}/system_stats");
                    if (probe.IsSuccessStatusCode) { Console.WriteLine(" ready."); return; }
                }
                catch { }
            }
            Console.WriteLine(" timeout — photos may fail until ComfyUI finishes loading.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ComfyUI] Failed to launch: {ex.Message}");
        }
    }

    public async Task<string> GenerateAsync(string sceneDescription, string? filePrefix = null, CancellationToken ct = default)
    {
        // 8GB handoff: free the LLM's VRAM before we load the 6.9GB checkpoint (no-op if the brain
        // is remote or not loaded). Without this, the model and SDXL can't both fit and gen OOMs.
        if (_releaseLlmVram is not null)
        {
            try { await _releaseLlmVram(ct); } catch { /* try to generate anyway */ }
        }

        try
        {
        var seed = Random.Shared.Next(1, int.MaxValue);
        var positive = $"{_options.QualityPrefix}{_options.Appearance}, {sceneDescription}";

        // Build a clean minimal workflow from scratch — no stale nodes from an exported JSON
        var workflow = new System.Text.Json.Nodes.JsonObject
        {
            ["1"] = BuildCheckpointNode(_options.Checkpoint),
            ["3"] = BuildLatentNode(512, 768),
            ["4"] = BuildClipNode(positive),
            ["5"] = BuildClipNode(_options.NegativePrompt),
            ["6"] = BuildKSamplerNode(seed),
            ["7"] = BuildVaeDecodeNode(),
            ["8"] = BuildSaveNode("Valerie")
        };

        string promptId;
        try
        {
            var payload = JsonSerializer.Serialize(new { prompt = workflow });
            var body = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_options.ComfyUrl}/prompt", body, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ComfyUI] Submit error {(int)resp.StatusCode}: {raw}");
                return "";
            }
            var json = JsonNode.Parse(raw);
            promptId = json!["prompt_id"]!.GetValue<string>();
        }
        catch (Exception ex) { Console.WriteLine($"[ComfyUI] Submit failed: {ex.Message}"); return ""; }

        Console.WriteLine($"[ComfyUI] Generating... (prompt {promptId[..8]})");

        string? filename = null;
        for (int i = 0; i < 300 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            try
            {
                var histResp = await _http.GetAsync($"{_options.ComfyUrl}/history/{promptId}", ct);
                var hist = JsonNode.Parse(await histResp.Content.ReadAsStringAsync(ct));
                var entry = hist?[promptId];
                if (entry == null) continue;

                var outputs = entry["outputs"];
                var status = entry["status"];
                bool done = outputs?["8"] != null
                    || status?["completed"]?.GetValue<bool>() == true
                    || status?["status_str"]?.GetValue<string>() == "success";

                if (done)
                {
                    filename = outputs?["8"]?["images"]?[0]?["filename"]?.GetValue<string>();
                    break;
                }
            }
            catch { /* transient, keep polling */ }
        }

        if (string.IsNullOrEmpty(filename))
        {
            Console.WriteLine("[ComfyUI] Timed out waiting for image.");
            return "";
        }

        var outDir = ResolvePath(_options.OutputDir);
        Directory.CreateDirectory(outDir);

        var safe = Regex.Replace(sceneDescription, @"[^a-zA-Z0-9_-]", "_").ToLowerInvariant();
        if (safe.Length > 60) safe = safe[..60];
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var destName = filePrefix != null
            ? $"{filePrefix}_{timestamp}.png"
            : $"valerie_{timestamp}_{safe}.png";
        var destPath = Path.Combine(outDir, destName);

        try
        {
            var imageBytes = await _http.GetByteArrayAsync(
                $"{_options.ComfyUrl}/view?filename={Uri.EscapeDataString(filename)}&type=output", ct);
            await File.WriteAllBytesAsync(destPath, imageBytes, ct);
            Console.WriteLine($"[Valerie sent a photo — saved to: {destName}]");
            Process.Start(new ProcessStartInfo("explorer.exe", outDir) { UseShellExecute = false });
        }
        catch (Exception ex) { Console.WriteLine($"[ComfyUI] Download failed: {ex.Message}"); return ""; }

        return destPath;
        }
        finally
        {
            // Hand the GPU back: drop ComfyUI's models so the brain can reload on the next message.
            await FreeComfyVramAsync();
        }
    }

    /// <summary>Ask ComfyUI to release its VRAM after a selfie so the LLM can reload cleanly.</summary>
    private async Task FreeComfyVramAsync()
    {
        try
        {
            var body = new StringContent("{\"unload_models\":true,\"free_memory\":true}",
                Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync($"{_options.ComfyUrl}/free", body, CancellationToken.None);
        }
        catch { /* best effort — --disable-smart-memory also frees on its own */ }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        // cwd is the project root when using dotnet run — check it first
        var fromCwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (File.Exists(fromCwd) || Directory.Exists(fromCwd)) return fromCwd;
        // walk up from BaseDirectory; trim trailing slash so GetDirectoryName actually ascends
        var baseDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 5; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDir, path));
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(baseDir);
            if (parent is null) break;
            baseDir = parent;
        }
        return fromCwd;
    }

    private static JsonNode BuildCheckpointNode(string ckpt) => JsonNode.Parse(
        $"{{\"inputs\":{{\"ckpt_name\":\"{ckpt}\"}},\"class_type\":\"CheckpointLoaderSimple\"}}")!;

    private static JsonNode BuildClipNode(string text) => JsonNode.Parse(
        $"{{\"inputs\":{{\"text\":{JsonSerializer.Serialize(text)},\"clip\":[\"1\",1]}},\"class_type\":\"CLIPTextEncode\"}}")!;

    private static JsonNode BuildLatentNode(int w, int h) => JsonNode.Parse(
        $"{{\"inputs\":{{\"width\":{w},\"height\":{h},\"batch_size\":1}},\"class_type\":\"EmptyLatentImage\"}}")!;

    private static JsonNode BuildKSamplerNode(int seed) => JsonNode.Parse(
        $"{{\"inputs\":{{\"seed\":{seed},\"steps\":28,\"cfg\":5,\"sampler_name\":\"euler\",\"scheduler\":\"normal\",\"denoise\":1,\"model\":[\"1\",0],\"positive\":[\"4\",0],\"negative\":[\"5\",0],\"latent_image\":[\"3\",0]}},\"class_type\":\"KSampler\"}}")!;

    private static JsonNode BuildVaeDecodeNode() => JsonNode.Parse(
        "{\"inputs\":{\"samples\":[\"6\",0],\"vae\":[\"1\",2]},\"class_type\":\"VAEDecode\"}")!;

    private static JsonNode BuildSaveNode(string prefix) => JsonNode.Parse(
        $"{{\"inputs\":{{\"filename_prefix\":\"{prefix}\",\"images\":[\"7\",0]}},\"class_type\":\"SaveImage\"}}")!;
}
