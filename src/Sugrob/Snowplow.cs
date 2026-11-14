using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using RECT = Sugrob.Native.RECT;

namespace Sugrob;

public enum PlowKind { Off = 0, Tractor = 1, Retro = 2, Snowmobile = 3, Penguin = 4, Custom = 5 }

/// <summary>
/// A snowplow that drives along the bottom drift of a monitor (taskbar edge or screen bottom),
/// pushing the snow into a growing pile in front of its blade and carrying it off-screen.
/// </summary>
internal sealed class Snowplow
{
    public PlowKind Kind { get; }
    public IntPtr DriftKey { get; }
    public RECT Monitor { get; }
    public bool Done { get; private set; }

    private float _x;          // blade front, virtual coords
    private readonly float _dir;
    private readonly float _s;
    private float _pile;       // area of pushed snow, px²
    private float _dist;       // distance travelled (wheel rotation, waddling)
    private float _shownR;     // smoothed pile size for drawing
    private float _speed;      // current speed (accelerates smoothly)
    private float _sprayAcc;

    private const float Length = 82f; // vehicle length in design units (× DPI scale)

    public Snowplow(PlowKind kind, Drift d, RECT monitor, float scale, float dir)
    {
        Kind = kind; DriftKey = d.Key; Monitor = monitor; _s = scale; _dir = dir;
        _x = dir > 0 ? monitor.Left : monitor.Right;
    }

    private float PileR => MathF.Sqrt(_pile / 1.7f);

    /// <summary>Blade front (virtual x) and travel direction — the app pushes loose snow there into the pile.</summary>
    public float BladeX => _x;
    public float Dir => _dir;
    public float Scale => _s;
    public void AddToPile(float area) => _pile += area;

    public void Update(float dt, World world, List<Spill> spill)
    {
        var d = world.GetDrift(DriftKey);
        if (d == null) { Done = true; return; }

        // a heavy pile slows the machine a little; it starts and stops smoothly
        float load = Math.Clamp(_pile / (1.7f * 22f * 22f * _s * _s), 0, 1);
        float target = 150f * _s * (1f - 0.3f * load);
        _speed += (target - _speed) * (1f - MathF.Exp(-dt * 2.5f));
        float nx = _x + _dir * _speed * dt;
        float a = MathF.Min(_x, nx) - d.Left, b = MathF.Max(_x, nx) - d.Left;
        // the blade scrapes almost to the edge: a thin layer stays behind, like after a real plow
        _pile += d.Take(a, b, 0.88f);
        _dist += MathF.Abs(nx - _x);
        _x = nx;

        // the pile can't grow forever: its front now and then gives way and a soft clump rolls off
        float maxR = 22f * _s;
        float maxPile = 1.7f * maxR * maxR;
        _sprayAcc += dt;
        if (_pile > maxPile && _sprayAcc > 0.35f)
        {
            _sprayAcc = 0;
            float r = PileR;
            float give = MathF.Min(_pile - maxPile * 0.85f, 900f * _s * _s);
            spill.Add(new Spill
            {
                X = _x + _dir * r * 2.0f, Y = d.Top - r * 0.7f, Area = give,
                Vx = _dir * (_speed + 60f * _s), Vy = -60f * _s, Source = DriftKey,
            });
            _pile -= give;
        }
        // the drawn pile follows the real one smoothly (no jumps)
        _shownR += (PileR - _shownR) * (1f - MathF.Exp(-dt * 6f));

        float tail = (Length + 30f) * _s + PileR * 2.5f;
        if (_dir > 0 ? _x - tail > Monitor.Right : _x + tail < Monitor.Left) Done = true;
    }

    // ------------------------------------------------------------------ render ------
    public void Render(Gpu gpu, Overlay o, World world, float time, CustomImage? image)
    {
        var d = world.GetDrift(DriftKey);
        if (d == null) return;
        var bo = o.Bounds;
        float fx = _x - bo.Left, gy = d.Top - bo.Top;
        if (fx < -400f * _s || fx > bo.Width + 400f * _s) return;
        var ctx = gpu.Ctx;
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;

        // draw only where its edge is visible (windows may cover parts of the taskbar edge)
        foreach (var seg in o.Segs)
        {
            if (seg.Drift.Key != DriftKey) continue;
            ctx.PushAxisAlignedClip(new RawRectF(seg.X0, 0, seg.X1, bo.Height), AntialiasMode.Aliased);
            Draw(gpu, fx, gy, time, image);
            ctx.PopAxisAlignedClip();
        }
        ctx.AntialiasMode = AntialiasMode.Aliased;
    }

    private void Draw(Gpu gpu, float fx, float gy, float time, CustomImage? image)
    {
        var ctx = gpu.Ctx;

        // pile of pushed snow in front of the blade: a soft mound leaning on the blade
        // and sloping smoothly down to the drift ahead
        float r = _shownR;
        if (r > 1f)
        {
            float h = MathF.Min(r * 1.4f, 27f * _s);    // can't be taller than the blade
            float len = r * 2.6f;
            using var g = gpu.D2DFactory.CreatePathGeometry();
            using (var sink = g.Open())
            {
                // a lumpy, churning top: snow rolls forward as the plow moves
                sink.BeginFigure(new Vector2(fx, gy + 1), FigureBegin.Filled);
                sink.AddLine(new Vector2(fx, gy - h * 0.92f));
                const int n = 14;
                for (int k = 1; k <= n; k++)
                {
                    float u = k / (float)n;
                    // base profile: high at the blade, rounded shoulder, long tail down to the drift
                    float prof = u < 0.35f ? 1f + 0.08f * MathF.Sin(u / 0.35f * MathF.PI) : MathF.Pow(1f - (u - 0.35f) / 0.65f, 1.6f);
                    float lump = 0.07f * MathF.Sin(u * 17f + _dist / _s * 0.12f) + 0.04f * MathF.Sin(u * 31f - _dist / _s * 0.2f);
                    float y = gy - h * MathF.Max(0, prof + lump * (1 - u));
                    sink.AddLine(new Vector2(fx + _dir * len * u, k == n ? gy + 1 : y));
                }
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            gpu.DriftBrush.StartPoint = new Vector2(0, gy - h);
            gpu.DriftBrush.EndPoint = new Vector2(0, gy);
            ctx.Transform = Matrix3x2.CreateTranslation(0, 1.5f * _s);
            ctx.FillGeometry(g, gpu.ShadowBrush);
            ctx.Transform = Matrix3x2.Identity;
            ctx.FillGeometry(g, gpu.DriftBrush);
        }

        // the machine rocks slightly on the uneven snow
        float bounce = (MathF.Sin(_dist / _s * 0.21f) * 0.6f + MathF.Sin(_dist / _s * 0.53f) * 0.4f) * 0.8f * _s;
        float tilt = MathF.Sin(_dist / _s * 0.17f) * 0.012f;
        var baseXf = Matrix3x2.CreateRotation(tilt) * Matrix3x2.CreateScale(_dir * _s, _s) * Matrix3x2.CreateTranslation(fx, gy - MathF.Abs(bounce));
        ctx.Transform = baseXf;
        var art = new PlowArt(ctx, gpu, baseXf);
        switch (Kind)
        {
            case PlowKind.Tractor: art.Tractor(_dist / _s, time); break;
            case PlowKind.Retro: art.Retro(_dist / _s, time); break;
            case PlowKind.Snowmobile: art.Snowmobile(_dist / _s, time); break;
            case PlowKind.Penguin: art.Penguin(_dist / _s); break;
            case PlowKind.Custom: art.BladeOnly(); break;
        }
        ctx.Transform = Matrix3x2.Identity;

        if (Kind == PlowKind.Custom && image != null)
        {
            // the picture itself is not mirrored, so text and faces stay readable
            float h = 52f * _s;
            float w = MathF.Min(h * image.Aspect, 130f * _s);
            h = w / image.Aspect;
            float bob = MathF.Abs(MathF.Sin(_dist / _s * 0.06f)) * 2f * _s;
            float x0 = _dir > 0 ? fx - 9f * _s - w : fx + 9f * _s;
            ctx.FillEllipse(new Ellipse(new Vector2(x0 + w / 2, gy), w * 0.45f, 3f * _s), gpu.ShadowBrush);
            ctx.DrawBitmap(image.Bitmap, new RawRectF(x0, gy - h - bob, x0 + w, gy - bob), 1f,
                Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
        }
    }
}

/// <summary>
/// Vector drawings of the plows, in design units: facing right, blade front at x=0,
/// ground at y=0 (up is negative). The caller's transform mirrors and scales them.
/// </summary>
internal readonly struct PlowArt
{
    private readonly ID2D1DeviceContext3 _ctx;
    private readonly Gpu _gpu;
    private readonly Matrix3x2 _base;

    public PlowArt(ID2D1DeviceContext3 ctx, Gpu gpu, Matrix3x2 baseXf) { _ctx = ctx; _gpu = gpu; _base = baseXf; }

    private static readonly Color4 Dark = new(0.18f, 0.18f, 0.2f, 1);
    private static readonly Color4 Tire = new(0.1f, 0.1f, 0.11f, 1);
    private static readonly Color4 Steel = new(0.6f, 0.66f, 0.74f, 1);
    private static readonly Color4 SteelHi = new(0.88f, 0.92f, 0.96f, 1);
    private static readonly Color4 Glass = new(0.62f, 0.82f, 0.95f, 1);
    private static readonly Color4 White = new(1, 1, 1, 1);
    private static readonly Color4 Orange = new(0.98f, 0.55f, 0.08f, 1);

    private ID2D1SolidColorBrush B(Color4 c) { _gpu.Paint.Color = c; return _gpu.Paint; }
    private void R(float x0, float y0, float x1, float y1, Color4 c) => _ctx.FillRectangle(new RawRectF(x0, y0, x1, y1), B(c));
    private void RR(float x0, float y0, float x1, float y1, float r, Color4 c) =>
        _ctx.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x0, y0, x1, y1), r, r), B(c));
    private void C(float x, float y, float r, Color4 c) => _ctx.FillEllipse(new Ellipse(new Vector2(x, y), r, r), B(c));
    private void E(float x, float y, float rx, float ry, Color4 c) => _ctx.FillEllipse(new Ellipse(new Vector2(x, y), rx, ry), B(c));
    private void L(float x0, float y0, float x1, float y1, float w, Color4 c) =>
        _ctx.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), B(c), w);
    private void P(string key, Color4 c, params Vector2[] pts) => _ctx.FillGeometry(_gpu.Polygon(key, pts), B(c));

    private void Wheel(float x, float y, float r, float dist, Color4 rim, Color4 spoke)
    {
        C(x, y, r, Tire);
        C(x, y, r * 0.58f, rim);
        float a = dist / r;
        for (int k = 0; k < 4; k++)
        {
            float ang = a + k * MathF.PI / 2;
            L(x, y, x + MathF.Cos(ang) * r * 0.52f, y + MathF.Sin(ang) * r * 0.52f, 1.4f, spoke);
        }
        C(x, y, r * 0.17f, Dark);
        // tread bumps
        for (int k = 0; k < 10; k++)
        {
            float ang = a + k * MathF.Tau / 10;
            C(x + MathF.Cos(ang) * r * 0.93f, y + MathF.Sin(ang) * r * 0.93f, r * 0.09f, new Color4(0.22f, 0.22f, 0.24f, 1));
        }
    }

    private void Smoke(float x, float y, float t)
    {
        for (int k = 0; k < 3; k++)
        {
            float ph = (t * 0.8f + k / 3f) % 1f;
            C(x - ph * 12f, y - ph * 24f, 2.5f + ph * 6f, new Color4(0.92f, 0.92f, 0.95f, 0.45f * (1 - ph)));
        }
    }

    private void Blade(float top = -30)
    {
        RR(-8, top, 0, -1, 1.5f, Steel);
        L(-1.6f, top + 1.5f, -1.6f, -2.5f, 1.1f, SteelHi);
        for (float y = top + 6; y < -3; y += 7) L(-7, y, -2.5f, y, 0.6f, new Color4(0.45f, 0.5f, 0.58f, 1));
    }

    public void BladeOnly()
    {
        R(-16, -16, -7, -12, Dark);
        Blade(-28);
    }

    public void Tractor(float dist, float t)
    {
        R(-66, -22, -18, -12, Dark);                          // chassis
        Wheel(-56, -16, 16, dist, new Color4(0.85f, 0.85f, 0.87f, 1), new Color4(0.45f, 0.45f, 0.48f, 1));
        Wheel(-24, -10, 10, dist, new Color4(0.85f, 0.85f, 0.87f, 1), new Color4(0.45f, 0.45f, 0.48f, 1));
        RR(-46, -34, -16, -17, 3, Orange);                    // hood
        for (float y = -31; y < -19; y += 3) L(-18.5f, y, -16.5f, y, 1, Dark); // grille
        RR(-76, -62, -44, -18, 3, Orange);                    // cabin
        RR(-72, -58, -48, -38, 2, Glass);                     // window
        L(-68, -56, -62, -42, 1.6f, new Color4(1, 1, 1, 0.55f));
        R(-78, -65, -42, -61, Dark);                          // roof
        R(-40, -50, -37, -34, new Color4(0.3f, 0.3f, 0.32f, 1)); // exhaust
        Smoke(-38.5f, -53, t);
        bool on = (t * 3f) % 1f < 0.5f;                       // beacon
        if (on) C(-60, -68, 7, new Color4(1, 0.7f, 0.1f, 0.3f));
        C(-60, -68, 3, on ? new Color4(1, 0.85f, 0.2f, 1) : new Color4(0.6f, 0.4f, 0.1f, 1));
        R(-18, -16, -7, -12, Dark);                           // blade arm
        Blade();
    }

    public void Retro(float dist, float t)
    {
        var green = new Color4(0.18f, 0.55f, 0.32f, 1);
        var red = new Color4(0.82f, 0.16f, 0.12f, 1);
        R(-64, -20, -20, -13, new Color4(0.25f, 0.18f, 0.12f, 1));
        Wheel(-56, -18, 18, dist, red, new Color4(1, 0.85f, 0.3f, 1));
        Wheel(-22, -9, 9, dist, red, new Color4(1, 0.85f, 0.3f, 1));
        RR(-78, -40, -36, -32, 4, green);                     // fender
        RR(-50, -37, -15, -18, 8, green);                     // rounded hood
        R(-18, -34, -15, -20, Dark);                          // radiator
        R(-31, -52, -27, -37, Dark);                          // chimney
        R(-33, -54, -25, -51, Dark);
        Smoke(-29, -56, t);
        R(-72, -46, -62, -41, Dark);                          // seat
        R(-72, -54, -69, -41, Dark);
        L(-50, -38, -45, -47, 1.6f, Dark);                    // steering
        L(-48, -49, -42, -45, 2.2f, Dark);
        RR(-68, -60, -56, -43, 3, new Color4(0.2f, 0.35f, 0.7f, 1)); // driver
        L(-58, -54, -47, -47, 3, new Color4(0.2f, 0.35f, 0.7f, 1));
        C(-62, -65, 5.5f, new Color4(0.98f, 0.82f, 0.68f, 1));
        RR(-68, -73, -56, -67, 2, red);                       // knitted hat
        C(-62, -74, 2.4f, White);
        R(-17, -16, -7, -12, Dark);
        Blade(-28);
    }

    public void Snowmobile(float dist, float t)
    {
        var red = new Color4(0.86f, 0.1f, 0.16f, 1);
        var jacket = new Color4(0.15f, 0.35f, 0.85f, 1);
        RR(-68, -11, -32, -1, 4, Tire);                        // track
        for (int k = 0; k < 4; k++)
        {
            float x = -63 + k * 9;
            C(x, -6, 3, new Color4(0.4f, 0.4f, 0.43f, 1));
            float a = dist / 3f;
            C(x + MathF.Cos(a) * 1.5f, -6 + MathF.Sin(a) * 1.5f, 0.8f, Dark);
        }
        L(-32, -1, -10, -1, 2.2f, Dark);                       // ski
        L(-10, -1, -5, -6, 2.2f, Dark);
        L(-22, -1, -26, -12, 2, Dark);                         // strut
        P("sm_body", red, new(-70, -11), new(-22, -11), new(-12, -18), new(-26, -29), new(-58, -27), new(-70, -19));
        P("sm_ws", new Color4(0.62f, 0.82f, 0.95f, 0.75f), new(-30, -28), new(-25, -39), new(-21, -38), new(-23, -25));
        RR(-68, -31, -48, -26, 2, Dark);                       // seat
        RR(-62, -52, -47, -28, 4, jacket);                     // rider
        L(-50, -44, -31, -33, 3.4f, jacket);
        C(-54, -58, 7.5f, new Color4(1, 0.82f, 0.1f, 1));      // helmet
        RR(-52, -61, -46.5f, -55, 1.5f, new Color4(0.2f, 0.2f, 0.26f, 1));
        C(-14, -17, 2.2f, new Color4(1, 1, 0.8f, 1));          // headlight
        L(-12, -14, -6, -12, 1.8f, Dark);
        Blade(-22);
    }

    public void Penguin(float dist)
    {
        float step = MathF.Sin(dist * 0.09f);
        float bob = -MathF.Abs(step) * 2f;
        var orange = new Color4(1f, 0.6f, 0.1f, 1);
        var black = new Color4(0.1f, 0.11f, 0.16f, 1);
        E(-31 + step * 3, -1.6f, 5.5f, 2.2f, orange);          // feet (don't bob)
        E(-20 - step * 3, -1.6f, 5.5f, 2.2f, orange);

        _ctx.Transform = Matrix3x2.CreateTranslation(0, bob) * _base;
        E(-25, -22, 12.5f, 20.5f, black);                      // body
        E(-21.5f, -20, 8, 15, White);                          // belly
        RR(-36, -35, -14, -30.5f, 2, new Color4(0.85f, 0.12f, 0.15f, 1)); // scarf
        RR(-35, -32, -30.5f, -21, 1.5f, new Color4(0.85f, 0.12f, 0.15f, 1));
        C(-19, -37, 3, White);                                 // eye
        C(-18.2f, -37, 1.5f, black);
        P("pg_beak", orange, new(-14.5f, -36), new(-7, -33.5f), new(-14.5f, -31.5f));
        L(-17, -26, -6, -7, 2.4f, new Color4(0.5f, 0.32f, 0.16f, 1)); // shovel handle
        L(-27, -26, -15, -18, 5, black);                       // flipper on the handle
        _ctx.Transform = _base;
        P("pg_shovel", Steel, new(-10, -11), new(0, -13), new(0, -1), new(-10, -1));
        L(-1.5f, -12, -1.5f, -2, 1, SteelHi);
    }
}

/// <summary>User picture loaded for the "custom" plow.</summary>
internal sealed class CustomImage : IDisposable
{
    public ID2D1Bitmap1 Bitmap { get; }
    public float Aspect { get; }
    public CustomImage(ID2D1Bitmap1 bitmap, float aspect) { Bitmap = bitmap; Aspect = aspect; }
    public void Dispose() => Bitmap.Dispose();
}
