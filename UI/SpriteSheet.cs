using SkiaSharp;

namespace Valerie.UI;

/// <summary>
/// Horizontal PNG sprite strip — frames packed left-to-right, all the same size.
/// TryLoad returns null silently if the file is missing; OrbWindow falls back to procedural.
/// </summary>
internal sealed class SpriteSheet : IDisposable
{
    private readonly SKBitmap[] _frames;

    public int FrameCount => _frames.Length;

    private SpriteSheet(SKBitmap[] frames) => _frames = frames;

    /// <summary>Load from a horizontal strip PNG. Returns null if missing or malformed.</summary>
    public static SpriteSheet? TryLoad(string path, int frameW, int frameH)
    {
        if (!File.Exists(path)) return null;

        using var strip = SKBitmap.Decode(path);
        if (strip is null || strip.Height < frameH || strip.Width < frameW) return null;

        int count  = strip.Width / frameW;
        var frames = new SKBitmap[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new SKBitmap(frameW, frameH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var c = new SKCanvas(frames[i]);
            c.DrawBitmap(strip,
                new SKRectI( i      * frameW, 0, (i + 1) * frameW, frameH),
                new SKRectI(0, 0, frameW, frameH));
        }
        return new SpriteSheet(frames);
    }

    public SKBitmap Frame(int index) => _frames[index % _frames.Length];

    public void Dispose()
    {
        foreach (var f in _frames) f.Dispose();
    }
}
