using System.Numerics;
using Vortice.Direct2D1;

namespace Sugrob;

/// <summary>Snow leaving a drift (virtual coords): becomes one or more soft clumps.</summary>
internal struct Spill
{
    public float X, Y, Area;
    public float Vx, Vy;
    public IntPtr Source; // drift it came from (doesn't land back on it right away)
}

/// <summary>
/// Soft clumps of snow: what the mitten pushes off, what a shaken or closed window drops,
/// what spills over the plow's pile. Each clump is a few overlapping round puffs (no corners,
/// no pixels), falls smoothly with gravity, stretches a little when falling fast and merges
/// into the drift it lands on.
/// </summary>
internal sealed class Clumps
{
    private sealed class Clump
    {
        public Vector2 P, V;
        public float R, Area, Age, Scale, Phase;
        public IntPtr Source;
        public Vector3[] Puffs = Array.Empty<Vector3>(); // x, y offset (×R) and radius (×R)
    }

    private const int MaxClumps = 120;
    private readonly List<Clump> _items = new();
    private readonly Random _rng = new();

    public int Count => _items.Count;

    /// <summary>Largest clump (px² of snow) — bigger amounts are split into several clumps.</summary>
    private static float MaxArea(float s) => 1500f * s * s;

    public void Add(in Spill sp, float scale)
    {
        if (sp.Area < 6f * scale * scale) return; // a pinch of snow isn't worth a clump
        float max = MaxArea(scale);
        int n = Math.Max(1, (int)MathF.Ceiling(sp.Area / max));
        float each = sp.Area / n;
        for (int k = 0; k < n; k++)
        {
            if (_items.Count >= MaxClumps) _items.RemoveAt(0);
            float r = MathF.Sqrt(each / MathF.PI) * 1.15f;
            var off = n == 1 ? Vector2.Zero : new Vector2((k - (n - 1) * 0.5f) * r * 1.6f, ((float)_rng.NextDouble() - 0.5f) * r);
            _items.Add(new Clump
            {
                P = new Vector2(sp.X, sp.Y) + off,
                V = new Vector2(sp.Vx, sp.Vy) * (0.85f + 0.3f * (float)_rng.NextDouble()),
                R = r, Area = each, Scale = scale, Source = sp.Source,
                Phase = (float)_rng.NextDouble() * 6.28f,
                Puffs = MakePuffs(),
            });
        }
    }

    private Vector3[] MakePuffs()
    {
        int n = 6 + _rng.Next(4);
        var p = new Vector3[n];
        p[0] = new Vector3(0, 0, 0.72f); // the core keeps it round
        for (int i = 1; i < n; i++)
        {
            float a = (float)_rng.NextDouble() * MathF.Tau;
            float d = 0.25f + 0.35f * (float)_rng.NextDouble();
            p[i] = new Vector3(MathF.Cos(a) * d, MathF.Sin(a) * d * 0.85f, 0.38f + 0.22f * (float)_rng.NextDouble());
        }
        return p;
    }

    public void Update(float dt, IReadOnlyList<Overlay> overlays, float bottomLimit)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var c = _items[i];
            c.Age += dt;
            float s = c.Scale;
            c.V.Y = MathF.Min(c.V.Y + 1100f * s * dt, 900f * s);   // gravity with a soft terminal speed
            c.V.X *= MathF.Exp(-1.2f * dt);                        // air slows sideways motion
            float prevBottom = c.P.Y + c.R * 0.8f;
            c.P += c.V * dt;
            float bottom = c.P.Y + c.R * 0.8f;

            if (c.V.Y > 0 && Land(c, prevBottom, bottom, overlays)) { _items.RemoveAt(i); continue; }
            if (c.P.Y - c.R * 2 > bottomLimit) _items.RemoveAt(i);
        }
    }

    private static bool Land(Clump c, float prevBottom, float bottom, IReadOnlyList<Overlay> overlays)
    {
        foreach (var o in overlays)
        {
            var b = o.Bounds;
            if (c.P.X < b.Left || c.P.X >= b.Right) continue;
            float lx = c.P.X - b.Left;
            foreach (var seg in o.Segs)
            {
                if (lx < seg.X0 || lx >= seg.X1) continue;
                var d = seg.Drift;
                if (d.Key == c.Source && c.Age < 0.35f) continue;
                float surface = b.Top + seg.Top - d.HeightAt(lx - seg.OriginX);
                if (prevBottom <= surface + 1 && bottom >= surface)
                {
                    // the clump flattens into the drift over its own width
                    float x0 = lx - seg.OriginX - c.R, x1 = lx - seg.OriginX + c.R;
                    int c0 = Math.Max(0, (int)(x0 / d.Cw)), c1 = Math.Min(d.H.Length - 1, (int)(x1 / d.Cw));
                    int n = Math.Max(1, c1 - c0 + 1);
                    for (int k = c0; k <= c1; k++) d.Deposit(k, c.Area / n);
                    return true;
                }
            }
        }
        return false;
    }

    public void Clear() => _items.Clear();

    /// <summary>Draws clumps as soft white puffs with the drifts' shading.</summary>
    public void Render(Gpu gpu, Overlay o, float time, Func<float, float, bool>? visible)
    {
        if (_items.Count == 0) return;
        var ctx = gpu.Ctx;
        var b = o.Bounds;
        bool any = false;
        foreach (var c in _items)
        {
            float lx = c.P.X - b.Left, ly = c.P.Y - b.Top;
            if (lx < -c.R * 2 || lx > b.Width + c.R * 2 || ly < -c.R * 2 || ly > b.Height + c.R * 2) continue;
            if (visible != null && !visible(lx, ly)) continue;
            if (!any) { ctx.AntialiasMode = AntialiasMode.PerPrimitive; any = true; }

            // slight stretch when falling fast, gentle wobble — it's soft snow, not a rock
            float stretch = 1f + MathF.Min(0.22f, MathF.Max(0, c.V.Y) / (4000f * c.Scale));
            float wob = 1f + 0.04f * MathF.Sin(time * 7f + c.Phase);
            float rx = c.R / stretch * wob, ry = c.R * stretch / wob;
            var center = new Vector2(lx, ly);

            gpu.DriftBrush.StartPoint = new Vector2(0, ly - ry);
            gpu.DriftBrush.EndPoint = new Vector2(0, ly + ry);
            // one soft shadow under the whole clump (per-puff shadows would darken the overlaps)
            ctx.FillEllipse(new Ellipse(center + new Vector2(0, 1.5f * c.Scale), rx * 0.95f, ry * 0.9f), gpu.ShadowBrush);
            foreach (var p in c.Puffs)
            {
                var pc = center + new Vector2(p.X * rx, p.Y * ry);
                ctx.FillEllipse(new Ellipse(pc, p.Z * rx, p.Z * ry), gpu.DriftBrush);
            }
        }
        if (any) ctx.AntialiasMode = AntialiasMode.Aliased;
    }
}
