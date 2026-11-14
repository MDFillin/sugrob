using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Sugrob;

/// <summary>
/// Base for live-wallpaper scenes. Static parts are drawn once into a bitmap;
/// dynamic parts (lights, smoke, twinkling) are drawn every wallpaper frame.
/// Layout unit U = 1% of the screen height, so scenes scale with any resolution.
/// </summary>
internal abstract class SceneArt : IDisposable
{
    public virtual void Dispose() { }

    protected float W, H, U, S;
    private ID2D1DeviceContext3 _ctx = null!;
    private Gpu _gpu = null!;

    public virtual void Layout(float w, float h, float scale) { W = w; H = h; U = h / 100f; S = scale; }
    public abstract void DrawStatic(ID2D1DeviceContext3 ctx, Gpu gpu);
    public abstract void DrawDynamic(ID2D1DeviceContext3 ctx, Gpu gpu, float t);
    public abstract void AddSprites(SpriteBuffer sb, Gpu gpu, float t);

    protected void Use(ID2D1DeviceContext3 ctx, Gpu gpu) { _ctx = ctx; _gpu = gpu; }

    // ---- drawing helpers ----------------------------------------------------------
    protected ID2D1SolidColorBrush B(Color4 c) { _gpu.Paint.Color = c; return _gpu.Paint; }
    protected void Rect(float x0, float y0, float x1, float y1, Color4 c) => _ctx.FillRectangle(new RawRectF(x0, y0, x1, y1), B(c));
    protected void RRect(float x0, float y0, float x1, float y1, float r, Color4 c) =>
        _ctx.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x0, y0, x1, y1), r, r), B(c));
    protected void Ell(float x, float y, float rx, float ry, Color4 c) => _ctx.FillEllipse(new Ellipse(new Vector2(x, y), rx, ry), B(c));
    protected void Line(float x0, float y0, float x1, float y1, float w, Color4 c) =>
        _ctx.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), B(c), w);

    protected void Poly(Color4 c, params Vector2[] pts) => Poly(B(c), pts);

    protected void Poly(ID2D1Brush brush, params Vector2[] pts)
    {
        using var g = _gpu.D2DFactory.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        _ctx.FillGeometry(g, brush);
    }

    protected ID2D1LinearGradientBrush LinGrad(Vector2 a, Vector2 b, params (float pos, Color4 c)[] stops)
    {
        using var coll = _ctx.CreateGradientStopCollection(stops.Select(s => new GradientStop(s.pos, s.c)).ToArray());
        return _ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(a, b), coll);
    }

    protected ID2D1RadialGradientBrush RadGrad(params (float pos, Color4 c)[] stops)
    {
        using var coll = _ctx.CreateGradientStopCollection(stops.Select(s => new GradientStop(s.pos, s.c)).ToArray());
        return _ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), coll);
    }

    protected void Glow(ID2D1RadialGradientBrush brush, float x, float y, float rx, float ry, float opacity)
    {
        brush.Center = new Vector2(x, y);
        brush.RadiusX = rx; brush.RadiusY = ry;
        brush.Opacity = opacity;
        _ctx.FillEllipse(new Ellipse(new Vector2(x, y), rx, ry), brush);
    }

    /// <summary>A snowy fir tree: three tiers with snow on each.</summary>
    protected void Pine(float x, float baseY, float h, Color4 body, Color4 snow)
    {
        Rect(x - h * 0.035f, baseY - h * 0.14f, x + h * 0.035f, baseY, new Color4(body.R * 0.7f, body.G * 0.6f, body.B * 0.6f, 1));
        for (int i = 0; i < 3; i++)
        {
            float bottom = baseY - h * (0.1f + 0.26f * i);
            float top = baseY - h * (0.48f + 0.26f * i);
            float half = h * (0.3f - 0.07f * i);
            Poly(body, new(x - half, bottom), new(x, top), new(x + half, bottom));
            // snow: a lighter band hugging the upper part of the tier
            Poly(snow, new(x - half * 0.55f, bottom - (bottom - top) * 0.42f), new(x, top),
                       new(x + half * 0.55f, bottom - (bottom - top) * 0.42f),
                       new(x + half * 0.2f, bottom - (bottom - top) * 0.36f), new(x - half * 0.15f, bottom - (bottom - top) * 0.4f));
        }
    }

    /// <summary>Wavy ridge line for hills.</summary>
    protected static float Ridge(float x, float w, float baseY, float amp, int seed) =>
        baseY + amp * (MathF.Sin(x / w * 6.1f + seed) * 0.5f + MathF.Sin(x / w * 13.7f + seed * 2.3f) * 0.3f
                       + MathF.Sin(x / w * 29.3f + seed * 0.7f) * 0.2f);

    protected Vector2[] RidgePoly(float baseY, float amp, int seed, float bottom)
    {
        var pts = new List<Vector2>();
        for (float x = 0; x <= W + 8; x += 8) pts.Add(new Vector2(x, Ridge(x, W, baseY, amp, seed)));
        pts.Add(new Vector2(W + 8, bottom));
        pts.Add(new Vector2(0, bottom));
        return pts.ToArray();
    }

    // ---- shared animated bits ------------------------------------------------------
    protected static readonly Color4[] BulbColors =
    {
        new(1.00f, 0.25f, 0.25f, 1), new(0.30f, 0.95f, 0.40f, 1), new(1.00f, 0.80f, 0.20f, 1),
        new(0.35f, 0.60f, 1.00f, 1), new(1.00f, 0.45f, 0.85f, 1),
    };

    protected void Bulbs(SpriteBuffer sb, Gpu gpu, List<Vector2> pts, float t, float size)
    {
        var glow = gpu.SpriteRects[0];
        var core = gpu.SpriteRects[1];
        for (int i = 0; i < pts.Count; i++)
        {
            var c = BulbColors[i % BulbColors.Length];
            float v = 0.5f + 0.5f * MathF.Sin(t * 3f - i * 0.5f);
            float b = 0.25f + 0.75f * v * v;
            float g = 0.8f * b;
            sb.Add(pts[i].X, pts[i].Y, size * 4.5f, glow, new Color4(c.R * g, c.G * g, c.B * g, g * 0.3f));
            float k = 0.45f + 0.55f * b, w = 0.4f * b;
            sb.Add(size * 0.55f, size * 0.75f, core,
                new Color4(MathF.Min(1, (c.R + w) * k), MathF.Min(1, (c.G + w) * k), MathF.Min(1, (c.B + w) * k), 1),
                Matrix3x2.CreateTranslation(pts[i]));
        }
    }

    /// <summary>Chimney smoke: a stateless loop of puffs (position is a function of time).</summary>
    protected void Smoke(SpriteBuffer sb, Gpu gpu, Vector2 src, float t, int puffs = 18)
    {
        var soft = gpu.SpriteRects[0];
        for (int k = 0; k < puffs; k++)
        {
            float life = (t * 0.12f + k / (float)puffs) % 1f;      // 0..1 over ~8 s
            float rise = life * 34f * U;
            float drift = life * life * 22f * U + MathF.Sin(t * 0.7f + k * 1.7f) * 1.5f * U * life;
            float size = (2.5f + 9f * life) * U;
            float a = 0.55f * (1f - life) * MathF.Min(1f, life * 8f);
            var p = src + new Vector2(drift, -rise);
            sb.Add(p.X, p.Y, size, soft, new Color4(0.85f * a, 0.87f * a, 0.92f * a, a));
        }
    }

    protected static float Flicker(float t, float seed) =>
        0.86f + 0.08f * MathF.Sin(t * 7.3f + seed) + 0.04f * MathF.Sin(t * 13.1f + seed * 2) + 0.02f * MathF.Sin(t * 23.7f);
}

// =====================================================================================
/// <summary>A log cabin in a snowy forest at night.</summary>
internal sealed class CabinScene : SceneArt
{
    private float _ground, _cx, _cw, _wallTop, _roofTop;
    private (float x0, float y0, float x1, float y1)[] _windows = Array.Empty<(float, float, float, float)>();
    private (float x0, float y0, float x1, float y1) _doorWin;
    private Vector2 _chimneyTop;
    private readonly List<Vector2> _bulbs = new();
    private readonly List<(Vector2 p, float r, float ph)> _twinkle = new();
    private ID2D1RadialGradientBrush? _warm;

    public override void Layout(float w, float h, float scale)
    {
        base.Layout(w, h, scale);
        _ground = 0.80f * H;
        _cx = W * 0.42f;
        _cw = 36 * U;
        _wallTop = _ground - 15 * U;
        _roofTop = _wallTop - 12 * U;
        float wy0 = _wallTop + 4 * U, wy1 = wy0 + 6.5f * U;
        _windows = new[]
        {
            (_cx - 14 * U, wy0, _cx - 7 * U, wy1),
            (_cx + 7 * U, wy0, _cx + 14 * U, wy1),
        };
        _doorWin = (_cx - 2 * U, _wallTop + 4 * U, _cx + 2 * U, _wallTop + 7 * U);
        _chimneyTop = new Vector2(_cx + 10 * U, _roofTop + 2 * U);

        // garland hanging along the eaves (under the roof edge)
        _bulbs.Clear();
        AddAlong(new Vector2(_cx - _cw / 2 - 3 * U, _wallTop + 1.2f * U), new Vector2(_cx + _cw / 2 + 3 * U, _wallTop + 1.2f * U), 2.2f * U);

        var rng = new Random(11);
        _twinkle.Clear();
        for (int i = 0; i < 40; i++)
            _twinkle.Add((new Vector2((float)rng.NextDouble() * W, (float)rng.NextDouble() * 0.55f * H),
                          (1.5f + (float)rng.NextDouble() * 2.5f) * S, (float)rng.NextDouble() * 10));
    }

    private void AddAlong(Vector2 a, Vector2 b, float step)
    {
        int n = (int)(Vector2.Distance(a, b) / step);
        for (int i = 0; i < n; i++)
        {
            var p = Vector2.Lerp(a, b, (i + 0.5f) / n);
            float sag = MathF.Sin((i % 4 + 0.5f) / 4f * MathF.PI) * 0.6f * U; // gentle drooping between hooks
            _bulbs.Add(p + new Vector2(0, sag + 0.9f * U));
        }
    }

    public override void DrawStatic(ID2D1DeviceContext3 ctx, Gpu gpu)
    {
        Use(ctx, gpu);
        // sky
        using (var sky = LinGrad(new(0, 0), new(0, 0.75f * H), (0, new(0.02f, 0.03f, 0.10f, 1)), (0.6f, new(0.05f, 0.09f, 0.22f, 1)), (1, new(0.13f, 0.18f, 0.35f, 1))))
            ctx.FillRectangle(new RawRectF(0, 0, W, H), sky);

        // stars
        var rng = new Random(7);
        for (int i = 0; i < 260; i++)
        {
            float x = (float)rng.NextDouble() * W, y = (float)Math.Pow(rng.NextDouble(), 1.4) * 0.62f * H;
            float r = (0.5f + (float)rng.NextDouble() * 1.1f) * S;
            Ell(x, y, r, r, new Color4(0.85f, 0.9f, 1f, 0.25f + 0.6f * (float)rng.NextDouble()));
        }

        // moon with halo
        var moon = new Vector2(0.8f * W, 0.17f * H);
        using (var halo = RadGrad((0, new(0.8f, 0.85f, 1f, 0.35f)), (0.35f, new(0.6f, 0.7f, 1f, 0.12f)), (1, new(0.5f, 0.6f, 1f, 0))))
            Glow(halo, moon.X, moon.Y, 16 * U, 16 * U, 1);
        Ell(moon.X, moon.Y, 4.2f * U, 4.2f * U, new Color4(0.97f, 0.96f, 0.88f, 1));
        Ell(moon.X - 1.2f * U, moon.Y - 0.8f * U, 0.9f * U, 0.8f * U, new Color4(0.88f, 0.87f, 0.8f, 1));
        Ell(moon.X + 1.4f * U, moon.Y + 1.3f * U, 0.6f * U, 0.6f * U, new Color4(0.9f, 0.89f, 0.82f, 1));

        // far mountains
        using (var mtn = LinGrad(new(0, 0.48f * H), new(0, 0.72f * H), (0, new(0.42f, 0.5f, 0.68f, 1)), (1, new(0.2f, 0.27f, 0.44f, 1))))
            Poly(mtn, RidgePoly(0.56f * H, 7 * U, 3, H));
        // far forest silhouette
        var far = new Color4(0.1f, 0.15f, 0.26f, 1);
        var farSnow = new Color4(0.38f, 0.46f, 0.62f, 1);
        for (float x = 0; x < W; x += 1.5f * U)
        {
            float h = (3 + (float)rng.NextDouble() * 4) * U;
            Pine(x + (float)rng.NextDouble() * U, Ridge(x, W, 0.665f * H, 1.5f * U, 9) + 0.5f * U, h, far, farSnow);
        }

        // mid hill
        using (var hill = LinGrad(new(0, 0.66f * H), new(0, H), (0, new(0.66f, 0.73f, 0.88f, 1)), (1, new(0.8f, 0.85f, 0.96f, 1))))
            Poly(hill, RidgePoly(0.71f * H, 2.5f * U, 5, H));
        var mid = new Color4(0.05f, 0.12f, 0.16f, 1);
        var midSnow = new Color4(0.78f, 0.84f, 0.95f, 1);
        foreach (var (fx, hh) in new[] { (0.06f, 16f), (0.11f, 12f), (0.16f, 18f), (0.21f, 11f), (0.62f, 13f), (0.68f, 17f), (0.74f, 12f), (0.86f, 19f), (0.93f, 14f) })
        {
            float x = fx * W;
            Pine(x, Ridge(x, W, 0.71f * H, 2.5f * U, 5) + 1.5f * U, hh * U, mid, midSnow);
        }

        // foreground snow
        using (var snow = LinGrad(new(0, 0.77f * H), new(0, H), (0, new(0.86f, 0.9f, 0.98f, 1)), (1, new(0.96f, 0.97f, 1f, 1))))
            Poly(snow, RidgePoly(0.795f * H, 0.8f * U, 2, H));

        DrawCabin();

        // a little snowman by the cabin
        float sx = _cx + 30 * U, sy = _ground + 3 * U;
        Ell(sx, sy + 0.4f * U, 4 * U, 1 * U, new Color4(0.6f, 0.66f, 0.8f, 0.5f));
        Ell(sx, sy - 2.6f * U, 3.2f * U, 3 * U, new Color4(0.95f, 0.96f, 1, 1));
        Ell(sx, sy - 7.2f * U, 2.3f * U, 2.2f * U, new Color4(0.95f, 0.96f, 1, 1));
        Ell(sx, sy - 10.6f * U, 1.6f * U, 1.6f * U, new Color4(0.95f, 0.96f, 1, 1));
        Ell(sx - 0.5f * U, sy - 11f * U, 0.2f * U, 0.2f * U, new Color4(0.1f, 0.1f, 0.1f, 1));
        Ell(sx + 0.5f * U, sy - 11f * U, 0.2f * U, 0.2f * U, new Color4(0.1f, 0.1f, 0.1f, 1));
        Poly(new Color4(1f, 0.55f, 0.1f, 1), new(sx, sy - 10.7f * U), new(sx + 1.6f * U, sy - 10.4f * U), new(sx, sy - 10.2f * U));
        RRect(sx - 1.8f * U, sy - 12.8f * U, sx + 1.8f * U, sy - 12.2f * U, 0.2f * U, new Color4(0.15f, 0.12f, 0.12f, 1));
        RRect(sx - 1.2f * U, sy - 14.6f * U, sx + 1.2f * U, sy - 12.5f * U, 0.3f * U, new Color4(0.15f, 0.12f, 0.12f, 1));

        // big foreground firs at the edges
        var near = new Color4(0.03f, 0.08f, 0.1f, 1);
        var nearSnow = new Color4(0.86f, 0.9f, 0.98f, 1);
        Pine(0.02f * W, H + 2 * U, 42 * U, near, nearSnow);
        Pine(0.97f * W, H + 3 * U, 46 * U, near, nearSnow);
        Pine(0.91f * W, H + 1 * U, 30 * U, near, nearSnow);
    }

    private void DrawCabin()
    {
        float x0 = _cx - _cw / 2, x1 = _cx + _cw / 2;
        var logLight = new Color4(0.47f, 0.28f, 0.16f, 1);
        var logDark = new Color4(0.28f, 0.16f, 0.09f, 1);

        Ell(_cx, _ground + 0.5f * U, _cw * 0.62f, 2.2f * U, new Color4(0.45f, 0.52f, 0.7f, 0.45f)); // shadow
        Rect(x0, _wallTop, x1, _ground, logLight);
        for (float y = _wallTop + 1.8f * U; y < _ground; y += 1.8f * U)
            Line(x0, y, x1, y, 0.35f * U, logDark);
        for (float y = _wallTop + 0.9f * U; y < _ground; y += 1.8f * U)
        {
            Ell(x0 - 0.3f * U, y, 0.9f * U, 0.85f * U, new Color4(0.6f, 0.4f, 0.24f, 1));
            Ell(x1 + 0.3f * U, y, 0.9f * U, 0.85f * U, new Color4(0.6f, 0.4f, 0.24f, 1));
        }

        // windows (dark interior; the warm light is drawn dynamically)
        foreach (var (a, b, c, d) in _windows)
        {
            Rect(a - 0.6f * U, b - 0.6f * U, c + 0.6f * U, d + 0.6f * U, new Color4(0.22f, 0.12f, 0.06f, 1));
            Rect(a, b, c, d, new Color4(0.3f, 0.18f, 0.08f, 1));
            RRect(a - 1f * U, d + 0.2f * U, c + 1f * U, d + 1.3f * U, 0.5f * U, new Color4(0.96f, 0.97f, 1, 1)); // snowy sill
        }

        // door
        float dx0 = _cx - 3 * U, dx1 = _cx + 3 * U;
        Rect(dx0 - 0.5f * U, _wallTop + 3.2f * U, dx1 + 0.5f * U, _ground, new Color4(0.22f, 0.12f, 0.06f, 1));
        Rect(dx0, _wallTop + 3.7f * U, dx1, _ground, new Color4(0.38f, 0.2f, 0.1f, 1));
        for (float x = dx0 + 1.5f * U; x < dx1; x += 1.5f * U) Line(x, _wallTop + 8 * U, x, _ground, 0.2f * U, new Color4(0.28f, 0.15f, 0.07f, 1));
        Ell(dx1 - 1.1f * U, _wallTop + 10.5f * U, 0.35f * U, 0.35f * U, new Color4(0.9f, 0.75f, 0.3f, 1));
        // wreath on the door
        var wr = new Vector2(_cx, _wallTop + 9.6f * U);
        for (int k = 0; k < 14; k++)
        {
            float ang = k * MathF.Tau / 14;
            Ell(wr.X + MathF.Cos(ang) * 1.1f * U, wr.Y + MathF.Sin(ang) * 1.1f * U, 0.55f * U, 0.55f * U, new Color4(0.1f, 0.38f, 0.18f, 1));
        }
        Ell(wr.X, wr.Y + 1.3f * U, 0.45f * U, 0.35f * U, new Color4(0.85f, 0.1f, 0.12f, 1));

        // chimney (behind the roof)
        Rect(_chimneyTop.X - 2 * U, _chimneyTop.Y, _chimneyTop.X + 2 * U, _wallTop - 3 * U, new Color4(0.38f, 0.35f, 0.38f, 1));
        for (float y = _chimneyTop.Y + 1.2f * U; y < _wallTop - 3 * U; y += 1.2f * U)
            Line(_chimneyTop.X - 2 * U, y, _chimneyTop.X + 2 * U, y, 0.15f * U, new Color4(0.26f, 0.24f, 0.27f, 1));
        RRect(_chimneyTop.X - 2.6f * U, _chimneyTop.Y - 1.2f * U, _chimneyTop.X + 2.6f * U, _chimneyTop.Y + 0.4f * U, 0.6f * U, new Color4(0.96f, 0.97f, 1, 1));

        // roof
        var roof = new Color4(0.32f, 0.12f, 0.1f, 1);
        Poly(roof, new(x0 - 3.5f * U, _wallTop + 0.6f * U), new(_cx, _roofTop), new(x1 + 3.5f * U, _wallTop + 0.6f * U),
                   new(x1 + 3.5f * U, _wallTop + 1.6f * U), new(x0 - 3.5f * U, _wallTop + 1.6f * U));
        // thick snow on the roof
        using (var rs = LinGrad(new(0, _roofTop - 2 * U), new(0, _wallTop), (0, new(1, 1, 1, 1)), (1, new(0.85f, 0.89f, 0.97f, 1))))
            Poly(rs, new(x0 - 4.2f * U, _wallTop + 0.4f * U), new(x0 - 3.2f * U, _wallTop - 0.9f * U), new(_cx, _roofTop - 2.4f * U),
                     new(x1 + 3.2f * U, _wallTop - 0.9f * U), new(x1 + 4.2f * U, _wallTop + 0.4f * U),
                     new(x1 + 2.4f * U, _wallTop - 0.2f * U), new(_cx, _roofTop - 0.6f * U), new(x0 - 2.4f * U, _wallTop - 0.2f * U));
        // icicles
        var rng = new Random(3);
        for (float x = x0 - 2.5f * U; x < x1 + 2.5f * U; x += 1.4f * U)
        {
            float len = (0.6f + (float)rng.NextDouble() * 1.8f) * U;
            Poly(new Color4(0.85f, 0.93f, 1f, 0.9f), new(x - 0.3f * U, _wallTop + 1.5f * U), new(x + 0.3f * U, _wallTop + 1.5f * U), new(x, _wallTop + 1.5f * U + len));
        }
        // garland wire
        for (int i = 1; i < _bulbs.Count; i++)
            Line(_bulbs[i - 1].X, _bulbs[i - 1].Y - 0.8f * U, _bulbs[i].X, _bulbs[i].Y - 0.8f * U, 0.18f * U, new Color4(0.08f, 0.14f, 0.08f, 1));
        // snow banked against the walls
        Poly(new Color4(0.92f, 0.95f, 1f, 1), new(x0 - 2 * U, _ground + 0.5f * U), new(x0 + 2 * U, _ground - 1.4f * U), new(dx0 - 0.5f * U, _ground - 0.6f * U),
             new(dx0 - 0.5f * U, _ground + 0.5f * U));
        Poly(new Color4(0.92f, 0.95f, 1f, 1), new(dx1 + 0.5f * U, _ground + 0.5f * U), new(dx1 + 0.5f * U, _ground - 0.6f * U), new(x1 - 2 * U, _ground - 1.6f * U),
             new(x1 + 2 * U, _ground + 0.5f * U));
    }

    public override void Dispose() { _warm?.Dispose(); _warm = null; }

    public override void DrawDynamic(ID2D1DeviceContext3 ctx, Gpu gpu, float t)
    {
        Use(ctx, gpu);
        _warm ??= RadGrad((0, new(1f, 0.72f, 0.32f, 0.55f)), (0.45f, new(1f, 0.6f, 0.25f, 0.18f)), (1, new(1f, 0.55f, 0.2f, 0)));
        for (int i = 0; i < _windows.Length; i++)
        {
            var (a, b, c, d) = _windows[i];
            float f = Flicker(t, i * 1.7f);
            // light spilling on the snow and the wall
            Glow(_warm, (a + c) / 2, (b + d) / 2, 13 * U, 10 * U, 0.7f * f);
            Glow(_warm, (a + c) / 2, _ground + 2.5f * U, 9 * U, 3 * U, 0.6f * f);
            Rect(a, b, c, (b + d) / 2, new Color4(1f, 0.8f * f, 0.42f * f, 1));
            Rect(a, (b + d) / 2, c, d, new Color4(0.97f * f, 0.62f * f, 0.25f * f, 1));
            // frame cross
            Line((a + c) / 2, b, (a + c) / 2, d, 0.5f * U, new Color4(0.22f, 0.12f, 0.06f, 1));
            Line(a, (b + d) / 2, c, (b + d) / 2, 0.5f * U, new Color4(0.22f, 0.12f, 0.06f, 1));
        }
        var (x0, y0, x1, y1) = _doorWin;
        float df = Flicker(t, 4.2f);
        Rect(x0, y0, x1, y1, new Color4(1f, 0.7f * df, 0.35f * df, 1));
        Line((x0 + x1) / 2, y0, (x0 + x1) / 2, y1, 0.3f * U, new Color4(0.22f, 0.12f, 0.06f, 1));
    }

    public override void AddSprites(SpriteBuffer sb, Gpu gpu, float t)
    {
        foreach (var (p, r, ph) in _twinkle)
        {
            float a = 0.25f + 0.75f * MathF.Max(0, MathF.Sin(t * 1.3f + ph));
            sb.Add(p.X, p.Y, r * 3f, gpu.SpriteRects[0], new Color4(0.85f * a, 0.9f * a, a, a * 0.6f));
        }
        Smoke(sb, gpu, _chimneyTop + new Vector2(0, -1.2f * U), t);
        Bulbs(sb, gpu, _bulbs, t, 1.1f * U);
    }
}

// =====================================================================================
/// <summary>A cosy porch: log wall, a lit window with a cat, a door with a wreath, a lantern.</summary>
internal sealed class PorchScene : SceneArt
{
    private (float x0, float y0, float x1, float y1) _win;
    private float _floor;
    private Vector2 _lantern;
    private readonly List<Vector2> _bulbs = new();
    private ID2D1RadialGradientBrush? _warm, _flame;

    public override void Layout(float w, float h, float scale)
    {
        base.Layout(w, h, scale);
        _floor = 86 * U;
        float wx = W * 0.16f;
        _win = (wx, 24 * U, wx + 40 * U, 62 * U);
        _lantern = new Vector2(W * 0.53f, 34 * U);

        // garland hanging under the beam
        _bulbs.Clear();
        float span = 16 * U;
        for (float x = 1.5f * U; x < W; x += 2.6f * U)
        {
            float u = (x % span) / span;
            _bulbs.Add(new Vector2(x, 9.6f * U + 3.2f * U * 4 * u * (1 - u)));
        }
    }

    public override void DrawStatic(ID2D1DeviceContext3 ctx, Gpu gpu)
    {
        Use(ctx, gpu);
        // log wall
        float logH = 6.5f * U;
        for (float y = 0; y < _floor; y += logH)
        {
            using var log = LinGrad(new(0, y), new(0, y + logH),
                (0, new(0.22f, 0.12f, 0.07f, 1)), (0.25f, new(0.42f, 0.25f, 0.14f, 1)),
                (0.6f, new(0.36f, 0.21f, 0.12f, 1)), (1, new(0.18f, 0.1f, 0.06f, 1)));
            ctx.FillRectangle(new RawRectF(0, y, W, y + logH), log);
        }
        var rng = new Random(5);
        for (int i = 0; i < 90; i++) // wood grain
        {
            float y = (float)rng.NextDouble() * _floor, x = (float)rng.NextDouble() * W, len = (4 + (float)rng.NextDouble() * 12) * U;
            Line(x, y, x + len, y + 0.2f * U, 0.12f * U, new Color4(0.2f, 0.11f, 0.06f, 0.5f));
        }

        // window: frame, (dynamic warm pane), curtains, frost, sill with snow and a cat
        var (a, b, c, d) = _win;
        Rect(a - 2 * U, b - 2 * U, c + 2 * U, d + 2 * U, new Color4(0.85f, 0.83f, 0.78f, 1));
        Rect(a - 1.2f * U, b - 1.2f * U, c + 1.2f * U, d + 1.2f * U, new Color4(0.65f, 0.62f, 0.56f, 1));

        // door
        float dx0 = W * 0.64f, dx1 = dx0 + 27 * U, dy0 = 22 * U;
        Rect(dx0 - 1.5f * U, dy0 - 1.5f * U, dx1 + 1.5f * U, _floor, new Color4(0.85f, 0.83f, 0.78f, 1));
        Rect(dx0, dy0, dx1, _floor, new Color4(0.45f, 0.13f, 0.12f, 1));
        for (float x = dx0 + 4.5f * U; x < dx1; x += 4.5f * U) Line(x, dy0, x, _floor, 0.3f * U, new Color4(0.32f, 0.08f, 0.08f, 1));
        RRect(dx0 + 3 * U, dy0 + 30 * U, dx0 + 24 * U, dy0 + 31.5f * U, 0.5f * U, new Color4(0.32f, 0.08f, 0.08f, 1));
        Ell(dx1 - 3.5f * U, 58 * U, 1 * U, 1 * U, new Color4(0.92f, 0.78f, 0.35f, 1));
        // wreath
        var wr = new Vector2((dx0 + dx1) / 2, 38 * U);
        for (int k = 0; k < 26; k++)
        {
            float ang = k * MathF.Tau / 26, rr = 6.2f * U;
            Ell(wr.X + MathF.Cos(ang) * rr, wr.Y + MathF.Sin(ang) * rr, 2.2f * U, 1.8f * U, new Color4(0.08f + 0.04f * (k % 3), 0.32f + 0.06f * (k % 2), 0.16f, 1));
        }
        for (int k = 0; k < 9; k++)
        {
            float ang = k * MathF.Tau / 9 + 0.3f, rr = 6.6f * U;
            Ell(wr.X + MathF.Cos(ang) * rr, wr.Y + MathF.Sin(ang) * rr, 0.75f * U, 0.75f * U, new Color4(0.85f, 0.08f, 0.12f, 1));
        }
        Poly(new Color4(0.85f, 0.1f, 0.12f, 1), new(wr.X, wr.Y + 6 * U), new(wr.X - 4 * U, wr.Y + 3.5f * U), new(wr.X - 4 * U, wr.Y + 8.5f * U));
        Poly(new Color4(0.85f, 0.1f, 0.12f, 1), new(wr.X, wr.Y + 6 * U), new(wr.X + 4 * U, wr.Y + 3.5f * U), new(wr.X + 4 * U, wr.Y + 8.5f * U));
        Poly(new Color4(0.75f, 0.06f, 0.1f, 1), new(wr.X - 0.8f * U, wr.Y + 6 * U), new(wr.X - 2.5f * U, wr.Y + 13 * U), new(wr.X - 1 * U, wr.Y + 12 * U));
        Poly(new Color4(0.75f, 0.06f, 0.1f, 1), new(wr.X + 0.8f * U, wr.Y + 6 * U), new(wr.X + 2.5f * U, wr.Y + 13 * U), new(wr.X + 1 * U, wr.Y + 12 * U));

        // lantern bracket
        Line(_lantern.X, 10 * U, _lantern.X, _lantern.Y - 5 * U, 0.4f * U, new Color4(0.12f, 0.12f, 0.13f, 1));

        // beam on top
        using (var beam = LinGrad(new(0, 0), new(0, 9 * U), (0, new(0.15f, 0.08f, 0.05f, 1)), (1, new(0.25f, 0.14f, 0.08f, 1))))
            ctx.FillRectangle(new RawRectF(0, 0, W, 9 * U), beam);
        Line(0, 9 * U, W, 9 * U, 0.4f * U, new Color4(0.1f, 0.05f, 0.03f, 1));
        // garland wire
        for (int i = 1; i < _bulbs.Count; i++)
            Line(_bulbs[i - 1].X, _bulbs[i - 1].Y - 1.1f * U, _bulbs[i].X, _bulbs[i].Y - 1.1f * U, 0.25f * U, new Color4(0.08f, 0.14f, 0.08f, 1));

        // floor boards with snow blown in at the edge
        using (var floor = LinGrad(new(0, _floor), new(0, H), (0, new(0.3f, 0.18f, 0.1f, 1)), (1, new(0.2f, 0.11f, 0.06f, 1))))
            ctx.FillRectangle(new RawRectF(0, _floor, W, H), floor);
        for (float y = _floor + 2.5f * U; y < H; y += 2.5f * U) Line(0, y, W, y, 0.2f * U, new Color4(0.15f, 0.08f, 0.04f, 1));
        using (var snowEdge = LinGrad(new(0, 92 * U), new(0, H), (0, new(0.92f, 0.95f, 1f, 1)), (1, new(0.82f, 0.88f, 0.97f, 1))))
            Poly(snowEdge, RidgePoly(95 * U, 1.5f * U, 4, H));

        // a red sled leaning against the wall
        float sx = W * 0.94f;
        Poly(new Color4(0.75f, 0.12f, 0.1f, 1), new(sx - 3 * U, _floor), new(sx - 1 * U, _floor), new(sx + 4 * U, 52 * U), new(sx + 2 * U, 52 * U));
        Line(sx - 4 * U, _floor + 0.5f * U, sx + 1.5f * U, 50 * U, 0.6f * U, new Color4(0.25f, 0.25f, 0.28f, 1));

        // railing in front
        var rail = new Color4(0.16f, 0.09f, 0.05f, 1);
        RRect(-U, 90 * U, W + U, 92.5f * U, 0.5f * U, rail);
        RRect(-U, 89 * U, W + U, 90.3f * U, 0.6f * U, new Color4(0.95f, 0.97f, 1f, 1)); // snow on the rail
        for (float x = 4 * U; x < W; x += 9 * U) Rect(x, 92 * U, x + 2.2f * U, H, rail);
    }

    public override void DrawDynamic(ID2D1DeviceContext3 ctx, Gpu gpu, float t)
    {
        Use(ctx, gpu);
        _warm ??= RadGrad((0, new(1f, 0.72f, 0.32f, 0.45f)), (0.5f, new(1f, 0.6f, 0.25f, 0.12f)), (1, new(1f, 0.55f, 0.2f, 0)));
        _flame ??= RadGrad((0, new(1f, 0.85f, 0.45f, 0.75f)), (0.3f, new(1f, 0.6f, 0.2f, 0.25f)), (1, new(1f, 0.5f, 0.2f, 0)));
        var (a, b, c, d) = _win;
        float f = Flicker(t, 0.3f);

        // glow around the window on the wall
        Glow(_warm, (a + c) / 2, (b + d) / 2, 45 * U, 35 * U, 0.75f * f);
        // warm interior: fireplace glow low on the left
        Rect(a, b, c, d, new Color4(0.85f * f, 0.52f * f, 0.22f * f, 1));
        Glow(_warm, a + 10 * U, d - 4 * U, 18 * U, 12 * U, f);
        // window bars
        var bar = new Color4(0.85f, 0.83f, 0.78f, 1);
        Rect((a + c) / 2 - 0.6f * U, b, (a + c) / 2 + 0.6f * U, d, bar);
        Rect(a, (b + d) / 2 - 0.6f * U, c, (b + d) / 2 + 0.6f * U, bar);
        // curtains
        var cur = new Color4(0.62f, 0.1f, 0.12f, 1);
        Poly(cur, new(a, b), new(a + 7 * U, b), new(a + 4 * U, (b + d) / 2), new(a + 6 * U, d), new(a, d));
        Poly(cur, new(c, b), new(c - 7 * U, b), new(c - 4 * U, (b + d) / 2), new(c - 6 * U, d), new(c, d));
        // frost in the corners (only on the glass)
        ctx.PushAxisAlignedClip(new RawRectF(a, b, c, d), AntialiasMode.Aliased);
        Glow(_frost ??= RadGrad((0, new(1, 1, 1, 0.75f)), (1, new(1, 1, 1, 0))), a, d, 9 * U, 7 * U, 1);
        Glow(_frost, c, d, 9 * U, 7 * U, 1);
        Glow(_frost, a, b, 6 * U, 5 * U, 0.8f);
        ctx.PopAxisAlignedClip();
        // sill with snow and a cat sitting on it (silhouette against the light)
        RRect(a - 3 * U, d + 1.5f * U, c + 3 * U, d + 3.5f * U, 0.5f * U, new Color4(0.85f, 0.83f, 0.78f, 1));
        RRect(a - 3 * U, d + 0.6f * U, c + 3 * U, d + 2.2f * U, 0.8f * U, new Color4(0.96f, 0.97f, 1f, 1));
        float kx = c - 13 * U, ky = d;
        var cat = new Color4(0.08f, 0.05f, 0.04f, 1);
        Ell(kx, ky - 3.6f * U, 3.2f * U, 3.8f * U, cat);
        Ell(kx + 0.3f * U, ky - 8.5f * U, 2.2f * U, 2 * U, cat);
        Poly(cat, new(kx - 1.9f * U, ky - 9.2f * U), new(kx - 1.4f * U, ky - 11.8f * U), new(kx - 0.1f * U, ky - 10.2f * U));
        Poly(cat, new(kx + 0.7f * U, ky - 10.2f * U), new(kx + 2 * U, ky - 11.8f * U), new(kx + 2.4f * U, ky - 9.2f * U));
        float tail = MathF.Sin(t * 1.4f) * 1.2f * U;
        Line(kx + 2.6f * U, ky - 0.8f * U, kx + 6 * U + tail, ky - 2.2f * U, 0.9f * U, cat);

        // lantern
        var L = _lantern;
        float lf = Flicker(t * 1.3f, 2.1f);
        Glow(_warm, L.X, L.Y, 22 * U, 20 * U, 0.9f * lf);
        RRect(L.X - 2.6f * U, L.Y - 5 * U, L.X + 2.6f * U, L.Y - 3.8f * U, 0.4f * U, new Color4(0.12f, 0.12f, 0.13f, 1));
        RRect(L.X - 2.2f * U, L.Y - 3.8f * U, L.X + 2.2f * U, L.Y + 3.4f * U, 0.5f * U, new Color4(1f, 0.85f * lf, 0.5f * lf, 0.85f));
        Glow(_flame, L.X, L.Y + 0.6f * U, 3 * U * lf, 4.2f * U * lf, 1);
        Ell(L.X, L.Y + 0.8f * U, 0.55f * U, 1.2f * U * lf, new Color4(1f, 0.95f, 0.75f, 1));
        Line(L.X - 2.2f * U, L.Y - 3.8f * U, L.X - 2.2f * U, L.Y + 3.4f * U, 0.4f * U, new Color4(0.12f, 0.12f, 0.13f, 1));
        Line(L.X + 2.2f * U, L.Y - 3.8f * U, L.X + 2.2f * U, L.Y + 3.4f * U, 0.4f * U, new Color4(0.12f, 0.12f, 0.13f, 1));
        RRect(L.X - 2.8f * U, L.Y + 3.2f * U, L.X + 2.8f * U, L.Y + 4.4f * U, 0.4f * U, new Color4(0.12f, 0.12f, 0.13f, 1));
    }

    private ID2D1RadialGradientBrush? _frost;

    public override void Dispose()
    {
        _warm?.Dispose(); _flame?.Dispose(); _frost?.Dispose();
        _warm = _flame = _frost = null;
    }

    public override void AddSprites(SpriteBuffer sb, Gpu gpu, float t)
    {
        // wire under the beam
        Bulbs(sb, gpu, _bulbs, t, 1.5f * U);
    }
}
