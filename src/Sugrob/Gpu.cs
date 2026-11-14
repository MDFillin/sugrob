using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Sugrob;

/// <summary>
/// Shared GPU objects: one D3D11 device, one D2D device context, one DirectComposition device
/// and a procedurally drawn sprite atlas with snowflakes. All overlays render through this.
/// </summary>
internal sealed class Gpu : IDisposable
{
    public const int SpriteSize = 64;
    public const int SpriteCount = 7;

    public ID3D11Device D3D { get; }
    public IDXGIDevice Dxgi { get; }
    public IDXGIFactory2 Factory { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public ID2D1DeviceContext3 Ctx { get; }
    public IDCompositionDevice Comp { get; }
    public ID2D1Bitmap1 Atlas { get; }
    public ID2D1SpriteBatch Batch { get; }

    /// <summary>Source rectangle of each sprite inside the atlas.</summary>
    public RawRect[] SpriteRects { get; } = new RawRect[SpriteCount];

    public Gpu()
    {
        var levels = new[] { D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0 };
        ID3D11Device device;
        try
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
        }
        catch
        {
            // Fallback: software rasterizer (very old/odd drivers, some RDP sessions).
            device = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, levels);
        }
        D3D = device;
        Dxgi = D3D.QueryInterface<IDXGIDevice>();
        Factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(Dxgi);
        using (var baseCtx = D2DDevice.CreateDeviceContext(DeviceContextOptions.None))
            Ctx = baseCtx.QueryInterface<ID2D1DeviceContext3>();
        Ctx.AntialiasMode = AntialiasMode.Aliased; // required by sprite batches
        Ctx.UnitMode = UnitMode.Pixels;

        Comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(Dxgi);

        Atlas = CreateAtlas();
        Batch = Ctx.CreateSpriteBatch();

        using var stops = Ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0f, new Color4(1f, 1f, 1f, 1f)),
            new GradientStop(0.55f, new Color4(0.95f, 0.97f, 1f, 1f)),
            new GradientStop(1f, new Color4(0.78f, 0.85f, 0.95f, 1f)),
        });
        DriftBrush = Ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, -20), Vector2.Zero), stops);
        ShadowBrush = Ctx.CreateSolidColorBrush(new Color4(0f, 0f, 0.05f, 0.22f));

        // snowballs / snowmen
        using var ballStops = Ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0f, new Color4(1f, 1f, 1f, 1f)),
            new GradientStop(0.6f, new Color4(0.93f, 0.95f, 0.99f, 1f)),
            new GradientStop(1f, new Color4(0.74f, 0.81f, 0.92f, 1f)),
        });
        BallBrush = Ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 10, 10), ballStops);
        BallEdgeBrush = Ctx.CreateSolidColorBrush(new Color4(0.62f, 0.7f, 0.84f, 0.55f));
        CoalBrush = Ctx.CreateSolidColorBrush(new Color4(0.12f, 0.12f, 0.14f, 1f));
        CarrotBrush = Ctx.CreateSolidColorBrush(new Color4(0.98f, 0.52f, 0.12f, 1f));
        StickBrush = Ctx.CreateSolidColorBrush(new Color4(0.42f, 0.27f, 0.14f, 1f));

        WireBrush = Ctx.CreateSolidColorBrush(new Color4(0.12f, 0.2f, 0.14f, 0.9f));
        Paint = Ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

        Carrot = D2DFactory.CreatePathGeometry();
        using (var sink = Carrot.Open())
        {
            sink.BeginFigure(new Vector2(0, -0.1f), FigureBegin.Filled);
            sink.AddLine(new Vector2(0.85f, 0.02f));
            sink.AddLine(new Vector2(0, 0.1f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
    }

    public ID2D1SolidColorBrush WireBrush { get; } = null!;
    /// <summary>General-purpose brush; set <c>Color</c> before use.</summary>
    public ID2D1SolidColorBrush Paint { get; } = null!;

    private readonly Dictionary<string, ID2D1PathGeometry> _geoCache = new();

    /// <summary>Polygon geometry built once and cached (local coordinates).</summary>
    public ID2D1PathGeometry Polygon(string key, params Vector2[] pts)
    {
        if (_geoCache.TryGetValue(key, out var g)) return g;
        g = D2DFactory.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        _geoCache[key] = g;
        return g;
    }

    public ID2D1RadialGradientBrush BallBrush { get; }
    public ID2D1SolidColorBrush BallEdgeBrush { get; }
    public ID2D1SolidColorBrush CoalBrush { get; }
    public ID2D1SolidColorBrush CarrotBrush { get; }
    public ID2D1SolidColorBrush StickBrush { get; }
    public ID2D1PathGeometry Carrot { get; }

    public ID2D1LinearGradientBrush DriftBrush { get; }
    public ID2D1SolidColorBrush ShadowBrush { get; }

    public ID2D1Bitmap1 CreateTargetBitmap(IDXGISwapChain1 swapChain)
    {
        using var surface = swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        return Ctx.CreateBitmapFromDxgiSurface(surface, props);
    }

    // ------------------------------------------------------------------------------
    // Sprite atlas: [0] soft far flake, [1] crisp round flake, [2],[3] crystals.
    // Pixels are premultiplied white, so tinting with (a,a,a,a) fades a sprite.
    // ------------------------------------------------------------------------------
    private unsafe ID2D1Bitmap1 CreateAtlas()
    {
        int w = SpriteSize * SpriteCount, h = SpriteSize;
        var px = new byte[w * h * 4];
        for (int s = 0; s < SpriteCount; s++)
        {
            SpriteRects[s] = new RawRect(s * SpriteSize, 0, (s + 1) * SpriteSize, SpriteSize);
            for (int y = 0; y < SpriteSize; y++)
            for (int x = 0; x < SpriteSize; x++)
            {
                // normalized coords, centre at 0, radius 1 at the sprite edge
                float nx = (x + 0.5f) / SpriteSize * 2f - 1f;
                float ny = (y + 0.5f) / SpriteSize * 2f - 1f;
                float a = SampleSprite(s, nx, ny);
                byte v = (byte)Math.Clamp(a * 255f + 0.5f, 0, 255);
                int i = (y * w + s * SpriteSize + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = v;
            }
        }

        fixed (byte* p = px)
        {
            var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96);
            return Ctx.CreateBitmap(new SizeI(w, h), (IntPtr)p, (uint)(w * 4), props);
        }
    }

    /// <summary>Coverage (0..1) of sprite <paramref name="s"/> at normalized coords (-1..1).</summary>
    public static float SampleSprite(int s, float nx, float ny, float thickScale = 1f) => s switch
    {
        0 => SoftDot(nx, ny),
        1 => CrispDot(nx, ny),
        2 => Crystal(nx, ny, 0, thickScale),
        3 => Crystal(nx, ny, 1, thickScale),
        4 => Star(nx, ny),
        5 => Fluffy(nx, ny),
        _ => RoundSquare(nx, ny),
    };

    /// <summary>A solid rounded square (loose snow cells merge into one soft surface).</summary>
    private static float RoundSquare(float x, float y)
    {
        const float half = 0.9f, r = 0.45f;
        float qx = MathF.Abs(x) - (half - r), qy = MathF.Abs(y) - (half - r);
        float outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        float d = outside + MathF.Min(MathF.Max(qx, qy), 0) - r; // signed distance
        return 1f - Smooth(-0.06f, 0.06f, d);
    }

    /// <summary>A twinkly star: six long thin rays, six short ones, bright core.</summary>
    private static float Star(float x, float y)
    {
        float d = float.MaxValue;
        for (int k = 0; k < 12; k++)
        {
            float ang = k * MathF.PI / 6f;
            float len = k % 2 == 0 ? 0.9f : 0.45f;
            d = MathF.Min(d, SegDist(x, y, Vector2.Zero, new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * len));
        }
        float r = MathF.Sqrt(x * x + y * y);
        float line = (1f - Smooth(0.01f, 0.07f, d)) * (1f - Smooth(0.5f, 0.95f, r) * 0.6f);
        float core = 1f - Smooth(0.05f, 0.2f, r);
        float glow = MathF.Exp(-(r * r) / (0.3f * 0.3f)) * 0.35f;
        return MathF.Min(1f, MathF.Max(line, core) + glow) * (1f - Smooth(0.95f, 1f, r));
    }

    /// <summary>A fluffy clump: several overlapping soft blobs.</summary>
    private static float Fluffy(float x, float y)
    {
        ReadOnlySpan<(float x, float y, float r)> blobs = stackalloc (float, float, float)[]
        {
            (0, 0, 0.42f), (0.32f, -0.2f, 0.3f), (-0.32f, -0.14f, 0.3f), (0.1f, 0.34f, 0.3f),
            (-0.2f, 0.3f, 0.26f), (0.36f, 0.22f, 0.24f), (-0.05f, -0.38f, 0.24f),
        };
        float a = 0;
        foreach (var b in blobs)
        {
            float d = MathF.Sqrt((x - b.x) * (x - b.x) + (y - b.y) * (y - b.y));
            a = MathF.Max(a, 1f - Smooth(b.r * 0.55f, b.r, d));
        }
        float r = MathF.Sqrt(x * x + y * y);
        return a * (0.85f + 0.15f * (1 - r)) * (1f - Smooth(0.95f, 1f, r));
    }

    private static float SoftDot(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y);
        if (r >= 1f) return 0f;
        float g = MathF.Exp(-(r * r) / (0.42f * 0.42f));
        return g * (1f - Smooth(0.85f, 1f, r));
    }

    private static float CrispDot(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y);
        float core = 1f - Smooth(0.38f, 0.52f, r);
        float glow = MathF.Exp(-(r * r) / (0.5f * 0.5f)) * 0.35f;
        return MathF.Min(1f, core + glow) * (1f - Smooth(0.9f, 1f, r));
    }

    private static float Crystal(float x, float y, int variant, float thickScale)
    {
        float d = float.MaxValue;
        const float arm = 0.86f;
        for (int k = 0; k < 6; k++)
        {
            float ang = k * MathF.PI / 3f + MathF.PI / 2f;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            d = MathF.Min(d, SegDist(x, y, Vector2.Zero, dir * arm));

            // side branches
            float[] at = variant == 0 ? [0.42f, 0.66f] : [0.34f, 0.55f, 0.74f];
            float[] len = variant == 0 ? [0.26f, 0.18f] : [0.2f, 0.17f, 0.11f];
            for (int b = 0; b < at.Length; b++)
            {
                var basePt = dir * at[b];
                for (int side = -1; side <= 1; side += 2)
                {
                    float ba = ang + side * MathF.PI / 3f;
                    var bdir = new Vector2(MathF.Cos(ba), MathF.Sin(ba));
                    d = MathF.Min(d, SegDist(x, y, basePt, basePt + bdir * len[b]));
                }
            }
        }
        float thick = (variant == 0 ? 0.055f : 0.045f) * thickScale;
        float line = 1f - Smooth(thick - 0.03f, thick + 0.03f, d);
        float r = MathF.Sqrt(x * x + y * y);
        float hub = 1f - Smooth(0.1f, 0.16f, r);
        float glow = MathF.Exp(-(r * r) / (0.35f * 0.35f)) * 0.25f;
        return MathF.Min(1f, MathF.Max(line, hub) + glow) * (1f - Smooth(0.95f, 1f, r));
    }

    private static float SegDist(float px, float py, Vector2 a, Vector2 b)
    {
        var p = new Vector2(px, py);
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        DriftBrush.Dispose();
        ShadowBrush.Dispose();
        BallBrush.Dispose();
        BallEdgeBrush.Dispose();
        CoalBrush.Dispose();
        CarrotBrush.Dispose();
        StickBrush.Dispose();
        Carrot.Dispose();
        WireBrush.Dispose();
        Paint.Dispose();
        foreach (var g in _geoCache.Values) g.Dispose();
        _geoCache.Clear();
        Batch.Dispose();
        Atlas.Dispose();
        Comp.Dispose();
        Ctx.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        Factory.Dispose();
        Dxgi.Dispose();
        D3D.Dispose();
    }
}
