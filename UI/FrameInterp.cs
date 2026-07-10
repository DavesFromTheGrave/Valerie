using SkiaSharp;

namespace Valerie.UI;

/// <summary>
/// Per-pixel color interpolation between hand-drawn keyframes.
/// Dave draws the start and middle of each animation; this fills in the rest.
///
/// Usage (from project root):
///   dotnet run -- gen-strip &lt;state&gt; &lt;stepsPerGap&gt; &lt;key0.png&gt; &lt;key1.png&gt; [key2.png ...]
///
///   state       : orb state name (idle, thinking, shocked, ...)
///   stepsPerGap : interpolated frames between each keyframe pair (4–8 is typical)
///   key*.png    : 220×220 PNGs with transparent background, one per keyframe
///
/// Output: UI/sprites/&lt;state&gt;_body.png  (horizontal strip, 220px rows)
/// </summary>
internal static class FrameInterp
{
    /// <summary>
    /// Pixel-lerp between consecutive keyframes.
    /// Each gap → keyframe + (stepsPerGap - 1) interpolated frames.
    /// With loop:true, also closes the gap from the last keyframe back to the first.
    /// </summary>
    public static SKBitmap[] Generate(SKBitmap[] keyframes, int stepsPerGap, bool loop = true)
    {
        if (keyframes.Length < 2) return [.. keyframes.Select(Clone)];

        var keys   = loop ? [.. keyframes, keyframes[0]] : keyframes;
        var result = new List<SKBitmap>(keys.Length * stepsPerGap);

        for (int k = 0; k < keys.Length - 1; k++)
        {
            result.Add(Clone(keys[k]));
            for (int s = 1; s < stepsPerGap; s++)
                result.Add(Lerp(keys[k], keys[k + 1], s / (float)stepsPerGap));
        }

        return [.. result];
    }

    /// <summary>Load PNGs from disk paths. Returns null if any path fails to decode.</summary>
    public static SKBitmap[]? LoadKeyframes(string[] paths)
    {
        var frames = new SKBitmap[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            frames[i] = SKBitmap.Decode(paths[i]);
            if (frames[i] is null)
            {
                for (int j = 0; j < i; j++) frames[j].Dispose();
                return null;
            }
        }
        return frames;
    }

    /// <summary>Save frames as a horizontal strip PNG (frame 0 at x=0, frame N at x=N*w).</summary>
    public static void SaveStrip(SKBitmap[] frames, string outputPath)
    {
        if (frames.Length == 0) return;
        int fw = frames[0].Width, fh = frames[0].Height;

        using var strip  = new SKBitmap(fw * frames.Length, fh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(strip);
        canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < frames.Length; i++)
            canvas.DrawBitmap(frames[i], i * fw, 0);

        using var img    = SKImage.FromBitmap(strip);
        using var data   = img.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static unsafe SKBitmap Lerp(SKBitmap a, SKBitmap b, float t)
    {
        int w = a.Width, h = a.Height;
        var result = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

        uint* pa = (uint*)a.GetPixels();
        uint* pb = (uint*)b.GetPixels();
        uint* po = (uint*)result.GetPixels();

        for (int i = 0, n = w * h; i < n; i++)
            po[i] = LerpPixel(pa[i], pb[i], t);

        return result;
    }

    // Lerp each byte channel independently. Using float avoids unsigned underflow.
    private static uint LerpPixel(uint a, uint b, float t)
        => L8( a        & 0xFF,  b        & 0xFF, t)
         | (uint)L8((a >>  8) & 0xFF, (b >>  8) & 0xFF, t) <<  8
         | (uint)L8((a >> 16) & 0xFF, (b >> 16) & 0xFF, t) << 16
         | (uint)L8((a >> 24) & 0xFF, (b >> 24) & 0xFF, t) << 24;

    private static byte L8(uint a, uint b, float t)
        => (byte)MathF.Round((float)a + ((float)b - (float)a) * t);

    private static SKBitmap Clone(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        src.CopyTo(dst);
        return dst;
    }
}
