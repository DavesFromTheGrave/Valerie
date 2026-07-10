using System.Runtime.InteropServices;
using SkiaSharp;

namespace Valerie.UI;

/// <summary>
/// Borderless, always-on-top, per-pixel transparent window.
/// Uses UpdateLayeredWindow so glow edges feather correctly against the desktop.
/// Drag with left mouse button to reposition.
/// </summary>
public sealed class OrbWindow : Form
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    private const int WS_EX_LAYERED    = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080; // no taskbar button
    private const int WM_NCHITTEST     = 0x0084;
    private const int HTCAPTION        = 2;           // makes whole form draggable
    private const uint ULW_ALPHA       = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool   UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("gdi32.dll")]  private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern bool   DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")]  private static extern bool   DeleteObject(IntPtr hObj);

    // ── Config ───────────────────────────────────────────────────────────────
    private const int CanvasSize = 220; // px — orb + glow margin

    // ── State ────────────────────────────────────────────────────────────────
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<OrbState, SpriteSheet> _sprites;
    private float _t;

    // ── Sprite slots (populated when real PNGs arrive) ───────────────────────
    // Each state can have a body strip and an expression overlay.
    // For now everything is procedural; replace with SKBitmap[] per state later.
    // private SKBitmap[]? _idleStrip;
    // private SKBitmap[]? _shockedBody;
    // private SKBitmap?   _shockedFace;
    // etc.

    // ── Construction ─────────────────────────────────────────────────────────
    public OrbWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        Size            = new Size(CanvasSize, CanvasSize);
        StartPosition   = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - CanvasSize - 30, screen.Bottom - CanvasSize - 30);

        _timer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        _timer.Tick += (_, _) => { _t += 0.033f; Render(); };
        _timer.Start();

        _sprites = LoadSprites();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // Makes the entire window draggable without a title bar
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTCAPTION; return; }
        base.WndProc(ref m);
    }

    // ── Sprite helpers ───────────────────────────────────────────────────────
    private static int GetFps(OrbState state) => state switch
    {
        OrbState.Spin or OrbState.Furious                  => 20,
        OrbState.Shocked or OrbState.Tremble or OrbState.BiPolar => 16,
        OrbState.Thinking                                  => 12, // 12 frames @ 12fps = 1s loop
        OrbState.Speaking                                  => 14,
        _                                                  => 10,
    };

    private static Dictionary<OrbState, SpriteSheet> LoadSprites()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "UI", "sprites"),
            Path.Combine(AppContext.BaseDirectory,        "UI", "sprites"),
        };
        var dir = candidates.FirstOrDefault(Directory.Exists);
        if (dir is null) return new();

        var dict = new Dictionary<OrbState, SpriteSheet>();
        foreach (OrbState s in Enum.GetValues<OrbState>())
        {
            var sheet = SpriteSheet.TryLoad(
                Path.Combine(dir, $"{s.ToString().ToLower()}_body.png"),
                CanvasSize, CanvasSize);
            if (sheet is not null) dict[s] = sheet;
        }
        return dict;
    }

    // ── Render ───────────────────────────────────────────────────────────────
    private void Render()
    {
        var ctrl = OrbController.Instance;

        using var bmp = new SKBitmap(CanvasSize, CanvasSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            DrawOrb(canvas, ctrl.State, ctrl.Amplitude, _t);
        }

        PushBitmap(bmp);
    }

    private void DrawOrb(SKCanvas canvas, OrbState state, float amplitude, float t)
    {
        float cx = CanvasSize / 2f;
        float cy = CanvasSize / 2f;
        float baseRadius = CanvasSize / 2f - 30f;

        // ── Speaking: grow + dim-bright with amplitude ───────────────────────
        float speakScale  = 1f + amplitude * 0.14f;
        float speakBright = amplitude;

        // ── Breathing (all non-static states) ────────────────────────────────
        float breathe = state != OrbState.Static
            ? 1f + MathF.Sin(t * 1.4f) * 0.03f
            : 1f;

        float radius = baseRadius * breathe * speakScale;

        // ── Sprite path — used once PNG strips exist for a state ─────────────
        if (_sprites.TryGetValue(state, out var sheet))
        {
            int  fi    = (int)(t * GetFps(state)) % sheet.FrameCount;
            var  frame = sheet.Frame(fi);
            float scale = breathe * speakScale;

            if (MathF.Abs(scale - 1f) > 0.002f)
            {
                float size   = CanvasSize * scale;
                float offset = (CanvasSize - size) / 2f;
                using var p  = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
                canvas.DrawBitmap(frame, new SKRect(offset, offset, offset + size, offset + size), p);
            }
            else
                canvas.DrawBitmap(frame, 0, 0);

            return;
        }

        // ── Procedural fallback (active until sprites are ready) ─────────────
        // ── Color palette per state ───────────────────────────────────────────
        var (core, glow) = state switch
        {
            OrbState.Listening       => (new SKColor( 30, 150, 230), new SKColor(  0, 100, 210,  90)),
            OrbState.Speaking        => (new SKColor( 50, 220, 185), new SKColor(  0, 210, 170, 110)),
            OrbState.Thinking        => (new SKColor( 40, 160, 170), new SKColor( 20, 130, 160,  80)),
            OrbState.Coder           => (new SKColor( 20,  15,  55), new SKColor(210,  90,  15, 110)), // dark navy core + warm orange glow
            OrbState.Shocked         => (new SKColor(200, 240, 200), new SKColor(230, 240,  80, 100)),
            OrbState.Tremble         => (new SKColor(220, 215,  30), new SKColor(255, 240,   0, 120)), // yellow
            OrbState.Skull           => (new SKColor(225, 215, 195), new SKColor(200, 190, 170,  55)), // bone/cream
            OrbState.Seto            => (new SKColor(230, 155, 135), new SKColor(220, 115,  90,  85)), // pink/flesh
            OrbState.Spin            => (new SKColor( 40, 200, 160), new SKColor(  0, 180, 140,  80)),
            OrbState.EverythingIsFine=> (new SKColor(110,  45,  15), new SKColor(190,  70,  10,  80)), // dark rust + ember
            OrbState.Furious         => (new SKColor(210,  55,  10), new SKColor(255, 100,   0, 140)), // orange-red
            OrbState.Mad             => (new SKColor( 40, 180, 150), new SKColor(200,  80,  20,  90)), // teal + orange fire glow
            OrbState.Algiz           => (new SKColor( 50, 110,  80), new SKColor( 30,  90,  60,  60)), // dark teal-green
            OrbState.Bullseye        => (new SKColor( 30, 130, 110), new SKColor(210,  20,  30, 110)), // teal + red glow
            OrbState.Embarrassed     => (new SKColor( 10,   8,  15), new SKColor(255,  55, 160, 120)), // black + pink neon
            OrbState.Greed           => (new SKColor(125,  45, 195), new SKColor(175,  25, 235, 130)), // purple
            OrbState.Crying          => (new SKColor( 20,  70, 180), new SKColor(  0,  50, 200,  90)), // deep blue
            OrbState.PacMan          => (new SKColor(240, 225,  10), new SKColor(255, 240,   0,  90)), // bright yellow
            OrbState.Devil           => (new SKColor(210,  25,  25), new SKColor(255,   0,   0, 130)), // red
            OrbState.Muses           => (new SKColor(190, 165,  90), new SKColor(175, 145,  55,  70)), // tan/gold
            OrbState.DoTryHarder     => (new SKColor(220, 210, 190), new SKColor(160, 130, 100,  75)), // pale cream + warm shadow
            OrbState.BiPolar         => (new SKColor( 35,  15, 110), new SKColor(120,  20, 200, 140)), // deep indigo/galaxy
            OrbState.Static          => (new SKColor( 40, 200, 160), new SKColor(  0, 180, 140,   0)),
            _                        => (new SKColor( 40, 200, 160), new SKColor(  0, 180, 140,  80)), // Idle
        };

        // Brighten core toward white when speaking loud
        if (speakBright > 0.01f)
            core = Lerp(core, new SKColor(180, 255, 240), speakBright * 0.5f);

        // ── Outer glow passes ─────────────────────────────────────────────────
        int glowPasses = state == OrbState.Static ? 0 : 3;
        for (int i = glowPasses; i >= 1; i--)
        {
            float glowR = radius + i * 14f + speakBright * 10f;
            var   glowA = (byte)(glow.Alpha / i);
            using var gp = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy), glowR,
                    [glow.WithAlpha(glowA), SKColors.Transparent],
                    [0f, 1f],
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(cx, cy, glowR, gp);
        }

        // ── Core sphere ───────────────────────────────────────────────────────
        using var cp = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx - radius * 0.28f, cy - radius * 0.28f), radius * 1.3f,
                [SKColors.White.WithAlpha(160), core, core.WithAlpha(210)],
                [0f, 0.38f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(cx, cy, radius, cp);

        // ── Placeholder label for undrawn states ─────────────────────────────
        if (state is not (OrbState.Idle or OrbState.Speaking or OrbState.Listening or OrbState.Static))
        {
            using var tp = new SKPaint
            {
                Color       = SKColors.Black.WithAlpha(160),
                TextSize    = 13f,
                IsAntialias = true,
                TextAlign   = SKTextAlign.Center,
                FakeBoldText = true,
            };
            canvas.DrawText(state.ToString(), cx, cy + 5f, tp);
        }
    }

    // ── Win32 bitmap push ────────────────────────────────────────────────────
    private void PushBitmap(SKBitmap bmp)
    {
        var screenDC = GetDC(IntPtr.Zero);
        var memDC    = CreateCompatibleDC(screenDC);

        // Wrap SkiaSharp pixels (BGRA premul) directly — no copy
        using var gdiBmp = new System.Drawing.Bitmap(
            CanvasSize, CanvasSize, CanvasSize * 4,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb,
            bmp.GetPixels());

        var hBmp    = gdiBmp.GetHbitmap(System.Drawing.Color.FromArgb(0));
        var hOld    = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = CanvasSize, cy = CanvasSize };
        var ptSrc = new POINT { x = 0,    y = 0 };
        var ptDst = new POINT { x = Left, y = Top };
        var blend = new BLENDFUNCTION { BlendOp = 0, AlphaFormat = 1, SourceConstantAlpha = 255 };

        UpdateLayeredWindow(Handle, screenDC, ref ptDst, ref size, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, hOld);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red   + (b.Red   - a.Red)   * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue  + (b.Blue  - a.Blue)  * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            foreach (var s in _sprites.Values) s.Dispose();
        }
        base.Dispose(disposing);
    }
}
