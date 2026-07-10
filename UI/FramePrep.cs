using SkiaSharp;

namespace Valerie.UI;

/// <summary>
/// Pre-processing helpers for raw keyframe images before they go into FrameInterp.
/// Handles grid sheets (crop), background removal (edge flood-fill), and resize.
/// </summary>
internal static class FramePrep
{
    /// <summary>
    /// Split an NxM grid image into individual frame PNGs, remove background,
    /// and resize each to <paramref name="targetSize"/>×<paramref name="targetSize"/>.
    /// Frames are ordered left-to-right, top-to-bottom.
    /// </summary>
    public static void SplitSheet(string inputPath, int rows, int cols,
                                  int targetSize, int bgTolerance,
                                  out SKBitmap[] result)
    {
        using var raw    = SKBitmap.Decode(inputPath)
                        ?? throw new Exception($"Cannot decode: {inputPath}");
        using var src    = Normalize(raw);

        int fw = src.Width  / cols;
        int fh = src.Height / rows;

        result = new SKBitmap[rows * cols];
        int k  = 0;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var cell = new SKBitmap(fw, fh, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var cv = new SKCanvas(cell))
                cv.DrawBitmap(src,
                    new SKRectI(c * fw, r * fh, (c + 1) * fw, (r + 1) * fh),
                    new SKRectI(0, 0, fw, fh));

            RemoveBackground(cell, bgTolerance);

            var resized = cell.Resize(
                new SKImageInfo(targetSize, targetSize, SKColorType.Bgra8888, SKAlphaType.Premul),
                SKFilterQuality.High);
            cell.Dispose();

            result[k++] = resized ?? throw new Exception("Resize returned null");
        }
    }

    /// <summary>
    /// Single-image prep: remove background and resize to targetSize.
    /// Use for standalone orb PNGs (not sheets).
    /// </summary>
    public static SKBitmap PrepSingle(string inputPath, int targetSize, int bgTolerance = 35)
    {
        using var raw = SKBitmap.Decode(inputPath)
                     ?? throw new Exception($"Cannot decode: {inputPath}");
        var bmp = Normalize(raw);
        RemoveBackground(bmp, bgTolerance);

        var resized = bmp.Resize(
            new SKImageInfo(targetSize, targetSize, SKColorType.Bgra8888, SKAlphaType.Premul),
            SKFilterQuality.High);
        bmp.Dispose();
        return resized ?? throw new Exception("Resize returned null");
    }

    public static void SavePng(SKBitmap bmp, string path)
    {
        using var img    = SKImage.FromBitmap(bmp);
        using var data   = img.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    // Normalise to Bgra8888 premul so all pixel ops use the same layout.
    private static SKBitmap Normalize(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var cv = new SKCanvas(dst);
        cv.DrawBitmap(src, 0, 0);
        return dst;
    }

    // Edge flood-fill: flood from the image border inward, making any pixel within
    // tolerance of the corner colour fully transparent. Stops at orb edges.
    private static unsafe void RemoveBackground(SKBitmap bmp, int tolerance)
    {
        int w = bmp.Width, h = bmp.Height;
        uint* px = (uint*)bmp.GetPixels();

        // Sample background colour from the top-left corner area
        // (Bgra8888: pixel = (A<<24)|(R<<16)|(G<<8)|B)
        uint  bgPx = px[Math.Min(3, h - 1) * w + Math.Min(3, w - 1)];
        int   bgB  = (int)( bgPx        & 0xFF);
        int   bgG  = (int)((bgPx >>  8) & 0xFF);
        int   bgR  = (int)((bgPx >> 16) & 0xFF);
        int   tol2 = tolerance * tolerance;

        bool Matches(uint p)
        {
            int db = (int)( p        & 0xFF) - bgB;
            int dg = (int)((p >>  8) & 0xFF) - bgG;
            int dr = (int)((p >> 16) & 0xFF) - bgR;
            return db * db + dg * dg + dr * dr <= tol2;
        }

        var seen  = new bool[w * h];
        var queue = new Queue<int>(w * 4 + h * 4);

        void Seed(int x, int y)
        {
            int i = y * w + x;
            if (!seen[i] && Matches(px[i])) { seen[i] = true; queue.Enqueue(i); }
        }

        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 1; y < h - 1; y++) { Seed(0, y); Seed(w - 1, y); }

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            px[idx] = 0; // transparent
            int x = idx % w, y = idx / w;
            if (x > 0)     Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0)     Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }
    }
}
