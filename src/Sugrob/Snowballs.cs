using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Sugrob;

/// <summary>
/// Snowballs and snowmen. A ball is rolled along a drift (growing as it picks up snow),
/// can be picked up and dropped; a ball dropped onto another one stays on top — that's
/// how snowmen are built. Resting balls ride along with their window.
/// Coordinates are virtual-desktop physical pixels.
/// </summary>
internal sealed class Snowballs
{
    private enum State { Rolling, Held, Falling, Resting }

    private sealed class Ball
    {
        public Vector2 P, V, Grab;
        public float R, Angle, S; // S = DPI scale
        public State St;
        public IntPtr DriftKey;
        public float LocalX;
        public Ball? Below;
        public int Frame;          // last frame its position was resolved
        public bool HasAbove;
        public int Depth;          // 0 = base of a stack
    }

    private const int MaxBalls = 24;
    private readonly List<Ball> _balls = new();
    private Ball? _active;
    private int _frame;

    public int Count => _balls.Count;

    // ------------------------------------------------------------------ input -------
    /// <summary>Sculpt-mode input, called every frame with the cursor (virtual coords).</summary>
    public void Input(Vector2 c, Vector2 cursorVel, bool pressed, bool down, bool released, float dt,
                      World world, IReadOnlyList<Overlay> overlays)
    {
        if (pressed)
        {
            _active = HitBall(c);
            if (_active != null)
            {
                foreach (var b in _balls)
                    if (b.Below == _active) { b.Below = null; b.St = State.Falling; b.V = Vector2.Zero; }
                _active.St = State.Held;
                _active.Below = null;
                _active.Grab = _active.P - c;
            }
            else if (FindDriftAt(c, overlays, out var drift, out float s))
            {
                if (_balls.Count >= MaxBalls) _balls.RemoveAt(0);
                _active = new Ball
                {
                    R = 5f * s, S = s, St = State.Rolling, DriftKey = drift.Key,
                    LocalX = Math.Clamp(c.X - drift.Left, 0, drift.Width),
                };
                // the first handful of snow
                float area = drift.Take(_active.LocalX - 8 * s, _active.LocalX + 8 * s, 0.5f);
                Grow(_active, area);
                _balls.Add(_active);
            }
        }

        var a = _active;
        if (a == null) return;

        if (down)
        {
            if (a.St == State.Held)
            {
                a.P = c + a.Grab;
                a.V = cursorVel;
            }
            else if (a.St == State.Rolling)
            {
                var d = world.GetDrift(a.DriftKey);
                if (d == null) { a.St = State.Falling; a.V = Vector2.Zero; _active = null; return; }
                // dragging far upwards lifts the ball off the drift
                if (c.Y < a.P.Y - a.R * 2.5f - 20f * a.S)
                {
                    a.St = State.Held;
                    a.Grab = a.P - c;
                    return;
                }
                float target = Math.Clamp(c.X - d.Left, a.R * 0.5f, Math.Max(a.R * 0.5f, d.Width - a.R * 0.5f));
                float maxStep = 1500f * a.S * dt;
                float dx = Math.Clamp(target - a.LocalX, -maxStep, maxStep);
                a.LocalX += dx;
                a.Angle += dx / a.R;
                if (MathF.Abs(dx) > 0.01f)
                {
                    float frac = Math.Clamp(MathF.Abs(dx) / (2f * a.R), 0f, 1f) * 0.7f;
                    float taken = d.Take(a.LocalX - a.R * 0.7f, a.LocalX + a.R * 0.7f, frac);
                    Grow(a, taken);
                }
            }
        }

        if (released)
        {
            if (a.St == State.Held)
            {
                a.St = State.Falling;
                float lim = 700f * a.S;
                a.V = new Vector2(Math.Clamp(cursorVel.X * 0.3f, -lim, lim), Math.Clamp(cursorVel.Y * 0.3f, -lim, lim));
            }
            else if (a.St == State.Rolling) a.St = State.Resting;
            _active = null;
        }
    }

    private static void Grow(Ball b, float area)
    {
        if (area <= 0) return;
        float r2 = b.R * b.R + area * 0.9f / MathF.PI;
        b.R = MathF.Min(MathF.Sqrt(r2), 48f * b.S);
    }

    private Ball? HitBall(Vector2 c)
    {
        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            if (Vector2.DistanceSquared(b.P, c) <= b.R * b.R * 1.1f) return b;
        }
        return null;
    }

    /// <summary>A visible drift with enough snow right at (or just above) the cursor.</summary>
    private static bool FindDriftAt(Vector2 c, IReadOnlyList<Overlay> overlays, out Drift drift, out float scale)
    {
        foreach (var o in overlays)
        {
            var b = o.Bounds;
            if (c.X < b.Left || c.X >= b.Right || c.Y < b.Top || c.Y >= b.Bottom) continue;
            float lx = c.X - b.Left, ly = c.Y - b.Top;
            foreach (var seg in o.Segs)
            {
                if (lx < seg.X0 || lx >= seg.X1) continue;
                var d = seg.Drift;
                float h = d.HeightAt(lx - seg.OriginX);
                if (h < 1.5f * d.Scale) continue;
                float surface = seg.Top - h;
                if (ly >= surface - 16f * d.Scale && ly <= seg.Top + 4f * d.Scale)
                {
                    drift = d; scale = d.Scale;
                    return true;
                }
            }
        }
        drift = null!; scale = 1;
        return false;
    }

    // ------------------------------------------------------------------ physics -----
    public void Update(float dt, World world, IReadOnlyList<Overlay> overlays, float bottomLimit)
    {
        _frame++;
        foreach (var b in _balls) b.HasAbove = false;
        foreach (var b in _balls) if (b.Below != null && b.St == State.Resting) b.Below.HasAbove = true;

        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            if (b.St == State.Falling)
            {
                var prev = b.P;
                b.V.Y += 1400f * b.S * dt;
                b.V.X *= MathF.Exp(-0.8f * dt);
                b.P += b.V * dt;
                if (TryLandOnBall(b, prev) || TryLandOnDrift(b, prev, overlays)) continue;
                if (b.P.Y - b.R > bottomLimit) { _balls.RemoveAt(i); if (_active == b) _active = null; }
            }
        }

        foreach (var b in _balls) Resolve(b, world);
    }

    /// <summary>Positions resting/rolling balls (bases first, so stacks follow their base).</summary>
    private void Resolve(Ball b, World world)
    {
        if (b.Frame == _frame) return;
        b.Frame = _frame;
        b.Depth = 0;
        switch (b.St)
        {
            case State.Rolling:
            case State.Resting when b.Below == null:
            {
                var d = world.GetDrift(b.DriftKey);
                if (d == null) { b.St = State.Falling; b.V = Vector2.Zero; return; }
                b.LocalX = Math.Clamp(b.LocalX, 0, d.Width);
                b.P = new Vector2(d.Left + b.LocalX, d.Top - d.HeightAt(b.LocalX) - b.R * 0.9f);
                break;
            }
            case State.Resting:
            {
                var below = b.Below!;
                Resolve(below, world);
                if (below.St != State.Resting && below.St != State.Rolling)
                {
                    b.Below = null; b.St = State.Falling; b.V = Vector2.Zero;
                    return;
                }
                b.P = new Vector2(below.P.X, below.P.Y - below.R * 0.78f - b.R * 0.85f);
                b.DriftKey = below.DriftKey;
                b.Depth = below.Depth + 1;
                break;
            }
            default:
                b.Depth = 100; // held / falling balls are drawn on top
                break;
        }
    }

    private bool TryLandOnBall(Ball b, Vector2 prev)
    {
        if (b.V.Y <= 0) return false;
        foreach (var t in _balls)
        {
            if (t == b || t.St != State.Resting || t.HasAbove) continue;
            if (MathF.Abs(b.P.X - t.P.X) > t.R * 0.8f) continue;
            if (b.R > t.R * 1.2f) continue; // too big to sit on it
            float top = t.P.Y - t.R * 0.78f;
            float prevBottom = prev.Y + b.R * 0.85f, bottom = b.P.Y + b.R * 0.85f;
            if (prevBottom <= top + 2 && bottom >= top)
            {
                b.St = State.Resting;
                b.Below = t;
                t.HasAbove = true;
                b.V = Vector2.Zero;
                return true;
            }
        }
        return false;
    }

    private static bool TryLandOnDrift(Ball b, Vector2 prev, IReadOnlyList<Overlay> overlays)
    {
        if (b.V.Y <= 0) return false;
        foreach (var o in overlays)
        {
            var bo = o.Bounds;
            if (b.P.X < bo.Left || b.P.X >= bo.Right) continue;
            float lx = b.P.X - bo.Left;
            foreach (var seg in o.Segs)
            {
                if (lx < seg.X0 || lx >= seg.X1) continue;
                var d = seg.Drift;
                float surface = bo.Top + seg.Top - d.HeightAt(lx - seg.OriginX);
                float prevBottom = prev.Y + b.R * 0.9f, bottom = b.P.Y + b.R * 0.9f;
                if (prevBottom <= surface + 2 && bottom >= surface)
                {
                    b.St = State.Resting;
                    b.Below = null;
                    b.DriftKey = d.Key;
                    b.LocalX = b.P.X - d.Left;
                    b.V = Vector2.Zero;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Clear-all: every ball falls apart into soft clumps.</summary>
    public void CrumbleAll(List<Spill> spill)
    {
        var rng = Random.Shared;
        foreach (var b in _balls)
        {
            int n = b.R > 20f * b.S ? 3 : 2;
            for (int i = 0; i < n; i++)
            {
                float ang = i * MathF.Tau / n + (float)rng.NextDouble();
                var c = b.P + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * b.R * 0.45f;
                spill.Add(new Spill { X = c.X, Y = c.Y, Area = MathF.PI * b.R * b.R / n, Vx = MathF.Cos(ang) * 80f * b.S, Vy = -60f * b.S });
            }
        }
        _balls.Clear();
        _active = null;
    }

    // ------------------------------------------------------------------ render ------
    private static bool IsVisible(Ball b, Overlay o)
    {
        if (b.St != State.Resting) return true;
        var root = b;
        while (root.Below != null) root = root.Below;
        float lx = root.P.X - o.Bounds.Left;
        foreach (var seg in o.Segs)
            if (seg.Drift.Key == root.DriftKey && lx >= seg.X0 - root.R && lx < seg.X1 + root.R) return true;
        return false;
    }

    private readonly List<Ball> _drawOrder = new();

    public void Render(Gpu gpu, Overlay o)
    {
        if (_balls.Count == 0) return;
        var ctx = gpu.Ctx;
        var bo = o.Bounds;

        _drawOrder.Clear();
        foreach (var b in _balls)
        {
            if (b.P.X + b.R * 2 < bo.Left || b.P.X - b.R * 2 > bo.Right || b.P.Y + b.R < bo.Top || b.P.Y - b.R * 2 > bo.Bottom) continue;
            if (IsVisible(b, o)) _drawOrder.Add(b);
        }
        if (_drawOrder.Count == 0) return;
        _drawOrder.Sort((x, y) => x.Depth.CompareTo(y.Depth));

        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var b in _drawOrder)
        {
            var c = new Vector2(b.P.X - bo.Left, b.P.Y - bo.Top);
            float r = b.R, s = b.S;
            bool isHead = b.St == State.Resting && b.Below != null && !b.HasAbove;
            bool isBody = b.St == State.Resting && b.HasAbove;
            bool armsHere = isBody && IsUnderHead(b);

            if (b.St == State.Resting && b.Below == null)
                ctx.FillEllipse(new Ellipse(c + new Vector2(0, r * 0.88f), r * 0.95f, r * 0.2f), gpu.ShadowBrush);

            if (armsHere) DrawArms(ctx, gpu, c, r, s);

            var rb = gpu.BallBrush;
            rb.Center = c;
            rb.GradientOriginOffset = new Vector2(-r * 0.35f, -r * 0.4f);
            rb.RadiusX = rb.RadiusY = r * 1.08f;
            ctx.FillEllipse(new Ellipse(c, r, r), rb);
            ctx.DrawEllipse(new Ellipse(c, r, r), gpu.BallEdgeBrush, MathF.Max(1f, 0.9f * s));

            if (isHead) DrawFace(ctx, gpu, c, r);
            else if (isBody) DrawButtons(ctx, gpu, c, r);
            else
            {
                // rolling texture: a few dents that turn with the ball
                for (int k = 0; k < 3; k++)
                {
                    float ang = b.Angle + k * 2.094f;
                    var p = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r * 0.55f;
                    ctx.FillEllipse(new Ellipse(p, r * 0.09f, r * 0.07f), gpu.BallEdgeBrush);
                }
            }
        }
        ctx.AntialiasMode = AntialiasMode.Aliased;
    }

    private bool IsUnderHead(Ball b)
    {
        foreach (var t in _balls)
            if (t.Below == b && t.St == State.Resting && !t.HasAbove) return true;
        return false;
    }

    private static void DrawFace(ID2D1DeviceContext3 ctx, Gpu gpu, Vector2 c, float r)
    {
        float e = MathF.Max(1.2f, r * 0.09f);
        ctx.FillEllipse(new Ellipse(c + new Vector2(-r * 0.32f, -r * 0.22f), e, e * 1.1f), gpu.CoalBrush);
        ctx.FillEllipse(new Ellipse(c + new Vector2(r * 0.32f, -r * 0.22f), e, e * 1.1f), gpu.CoalBrush);
        // carrot
        ctx.Transform = Matrix3x2.CreateScale(r) * Matrix3x2.CreateTranslation(c.X + r * 0.05f, c.Y + r * 0.05f);
        ctx.FillGeometry(gpu.Carrot, gpu.CarrotBrush);
        ctx.Transform = Matrix3x2.Identity;
        // smile
        float m = MathF.Max(0.9f, r * 0.05f);
        for (int k = 0; k < 5; k++)
        {
            float ang = MathF.PI * (0.25f + 0.125f * k);
            ctx.FillEllipse(new Ellipse(c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r * 0.5f, m, m), gpu.CoalBrush);
        }
    }

    private static void DrawButtons(ID2D1DeviceContext3 ctx, Gpu gpu, Vector2 c, float r)
    {
        float e = MathF.Max(1.2f, r * 0.08f);
        for (int k = -1; k <= 1; k++)
            ctx.FillEllipse(new Ellipse(c + new Vector2(0, k * r * 0.38f - r * 0.05f), e, e), gpu.CoalBrush);
    }

    private static void DrawArms(ID2D1DeviceContext3 ctx, Gpu gpu, Vector2 c, float r, float s)
    {
        float w = MathF.Max(1.5f, r * 0.07f);
        for (int side = -1; side <= 1; side += 2)
        {
            var a = c + new Vector2(side * r * 0.7f, -r * 0.15f);
            var b = c + new Vector2(side * r * 1.65f, -r * 0.85f);
            ctx.DrawLine(a, b, gpu.StickBrush, w);
            var mid = Vector2.Lerp(a, b, 0.7f);
            ctx.DrawLine(mid, mid + new Vector2(side * r * 0.3f, r * 0.05f), gpu.StickBrush, w * 0.7f);
            ctx.DrawLine(mid, mid + new Vector2(side * r * 0.05f, -r * 0.3f), gpu.StickBrush, w * 0.7f);
        }
    }
}
