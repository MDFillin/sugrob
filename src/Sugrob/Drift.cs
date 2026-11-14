using System.Numerics;
using Vortice.Direct2D1;

namespace Sugrob;

public enum DriftKind { Window, Taskbar, Ground }

/// <summary>
/// A snowdrift lying on a horizontal edge (top of a window, the taskbar or the bottom of a monitor).
/// Stored as a heightmap of fixed-width columns, relative to the edge's left end, so it moves
/// together with its window for free. Coordinates are virtual-desktop physical pixels.
/// </summary>
internal sealed class Drift : IDisposable
{
    public IntPtr Key { get; }
    public DriftKind Kind { get; }
    public float Scale { get; private set; }
    public float Cw { get; private set; }      // column width, px
    public float MaxH { get; private set; }    // max height, px

    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public float Vx { get; private set; }      // edge velocity, px/s (for shaking off snow)
    public float Vy { get; private set; }

    public float[] H = Array.Empty<float>();
    public float CurMax { get; private set; }  // tallest column right now
    public int Version { get; private set; }   // incremented on every shape rebuild
    public bool Seen;

    private bool _dirty = true;
    private float _pending = 100; // accumulated shape change since the last rebuild, px
    private bool _relaxNeeded;
    private ID2D1GeometryRealization? _geo;
    private double _builtAt = -1;
    private bool _initialized;

    public Drift(IntPtr key, DriftKind kind) { Key = key; Kind = kind; }

    /// <summary>Updates position/size. Returns the area (px²) of snow that no longer fits (window shrank).</summary>
    public void Place(int left, int top, int width, float scale, float dt, List<Spill> spill)
    {
        if (!_initialized || Scale != scale)
        {
            Scale = scale;
            Cw = MathF.Max(3f, MathF.Round(3f * scale));
            MaxH = Kind switch
            {
                DriftKind.Ground => 42f * scale,
                DriftKind.Taskbar => 20f * scale,
                _ => 17f * scale,
            };
        }

        if (_initialized && dt > 0)
        {
            float vx = (left - Left) / dt, vy = (top - Top) / dt;
            Vx = vx; Vy = vy;
        }
        else { Vx = Vy = 0; }

        int cols = Math.Max(1, (int)MathF.Ceiling(width / Cw));
        if (cols != H.Length)
        {
            // Window resized: keep the left part, the cut-off snow falls down.
            var old = H;
            H = new float[cols];
            Array.Copy(old, H, Math.Min(old.Length, cols));
            if (old.Length > cols && _initialized)
            {
                Log?.Invoke($"shrink key={Key} {old.Length}->{cols} cols");
                EmitDebris(old, cols, old.Length, 1f, left, top, spill);
            }
            _dirty = _relaxNeeded = true;
            _pending += 100;
        }

        // Geometry is in local coords, so moving the window needs no rebuild.
        Left = left; Top = top; Width = width;
        _initialized = true;

        // Shaking the window (fast back-and-forth) throws off a part of its snow.
        if (Kind == DriftKind.Window && dt > 0) DetectShake(dt, spill);
    }

    private float _shake, _lastVx, _lastVy;

    private void DetectShake(float dt, List<Spill> spill)
    {
        _shake *= MathF.Exp(-dt * 1.5f);
        // A jump (maximize, restore, snap) is not shaking: forget the previous direction.
        if (MathF.Abs(Vx * dt) > 150f * Scale || MathF.Abs(Vy * dt) > 150f * Scale)
        {
            _lastVx = _lastVy = 0;
            return;
        }
        float fast = 600f * Scale;
        if (MathF.Abs(Vx) > fast)
        {
            if (MathF.Abs(_lastVx) > fast && MathF.Sign(Vx) != MathF.Sign(_lastVx)) _shake += 1f;
            _lastVx = Vx;
        }
        if (MathF.Abs(Vy) > fast)
        {
            if (MathF.Abs(_lastVy) > fast && MathF.Sign(Vy) != MathF.Sign(_lastVy)) _shake += 1f;
            _lastVy = Vy;
        }
        if (_shake >= 1.8f && CurMax > 0.5f)
        {
            Log?.Invoke($"shake key={Key} v=({Vx:F0},{Vy:F0}) dt={dt:F4} shake={_shake:F2}");
            ShakeOff(0.35f, spill);
            _shake = 0.8f; // keep shaking -> keep losing snow
        }
    }

    /// <summary>Column cap: snow rounds off towards the ends of the edge.</summary>
    private float Cap(int i)
    {
        float edge = MathF.Min(i, H.Length - 1 - i) * Cw;
        return MathF.Min(MaxH, edge * 0.9f + 1.5f * Scale);
    }

    /// <summary>Adds an amount of snow (area in px²) around column <paramref name="col"/>.</summary>
    public void Add(int col, float area)
    {
        // triangular spread over 7 columns: weights 1,2,3,4,3,2,1 / 16
        float h = area / Cw / 16f;
        for (int k = -3; k <= 3; k++)
        {
            int i = col + k;
            if ((uint)i >= (uint)H.Length) continue;
            float w = 4 - Math.Abs(k);
            H[i] = MathF.Min(H[i] + h * w, Cap(i));
        }
        _dirty = _relaxNeeded = true;
        _pending += h * 4;
    }

    /// <summary>
    /// Snow landing on the drift. If the drift is full right there, the snow slides to the
    /// nearest column with room (within ~12 columns); whatever still doesn't fit is lost — like snow
    /// sliding off a full roof. Returns 0 (the loose snow is always absorbed).
    /// </summary>
    public float Deposit(int col, float area) => Deposit(col, area, 12, absorbAll: true);

    /// <summary>
    /// Adds snow at a column, spilling to neighbours within <paramref name="reach"/> columns if full.
    /// With <paramref name="absorbAll"/> = false the part that doesn't fit is returned (loose snow
    /// then stays lying on top of the drift as a visible bump, as in the original app).
    /// </summary>
    public float Deposit(int col, float area, int reach, bool absorbAll)
    {
        float left = area;
        for (int step = 0; step <= reach && left > 0.01f; step++)
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                if (step == 0 && sgn > 0) continue;
                int i = col + step * sgn;
                if ((uint)i >= (uint)H.Length) continue;
                float room = (Cap(i) - H[i]) * Cw;
                if (room <= 0.01f) continue;
                float put = MathF.Min(room, left);
                H[i] += put / Cw;
                left -= put;
                if (left <= 0.01f) break;
            }
        }
        if (left < area) { _dirty = _relaxNeeded = true; _pending += (area - left) / Cw; }
        return absorbAll ? 0 : MathF.Max(0, left);
    }

    /// <summary>Removes up to <paramref name="amount"/> px of height from one column; returns removed height.</summary>
    public float TakeColumn(int i, float amount)
    {
        if ((uint)i >= (uint)H.Length) return 0;
        float t = MathF.Min(H[i], amount);
        if (t <= 0) return 0;
        H[i] -= t;
        _dirty = _relaxNeeded = true;
        _pending += t;
        return t;
    }

    public float HeightAt(float localX)
    {
        if (H.Length == 0) return 0;
        int c = Math.Clamp((int)(localX / Cw), 0, H.Length - 1);
        return H[c];
    }

    /// <summary>Removes all snow (the "clear everything" command).</summary>
    public void Clear()
    {
        Array.Clear(H);
        CurMax = 0;
        _dirty = _relaxNeeded = true;
        _pending += 100;
    }

    /// <summary>Takes snow from under a rolling snowball. Returns the area taken.</summary>
    public float Take(float x0, float x1, float fraction)
    {
        int c0 = Math.Max(0, (int)(x0 / Cw)), c1 = Math.Min(H.Length - 1, (int)(x1 / Cw));
        float taken = 0;
        for (int i = c0; i <= c1; i++)
        {
            float t = H[i] * fraction;
            H[i] -= t;
            taken += t * Cw;
        }
        if (taken > 0) { _dirty = _relaxNeeded = true; _pending += 100; }
        return taken;
    }

    /// <summary>Lets steep slopes slide (one relaxation pass per frame).</summary>
    public void Relax()
    {
        if (!_relaxNeeded) return;
        _relaxNeeded = false;
        float limit = Cw * 0.4f; // max height difference between neighbours (angle of repose)
        bool moved = false;
        float maxMove = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < H.Length - 1; i++)
            {
                float d = H[i] - H[i + 1];
                float t = 0;
                if (d > limit + 0.01f) { t = (d - limit) * 0.5f; H[i] -= t; H[i + 1] += t; }
                else if (-d > limit + 0.01f) { t = (-d - limit) * 0.5f; H[i + 1] -= t; H[i] += t; }
                if (t > 0) { moved = true; if (t > maxMove) maxMove = t; }
            }
        }
        _pending += maxMove;
        float max = 0;
        for (int i = 0; i < H.Length; i++)
        {
            float c = Cap(i);
            if (H[i] > c) H[i] = c;
            if (H[i] > max) max = H[i];
        }
        CurMax = max;
        if (moved) _relaxNeeded = true; // keep settling next frame
        _dirty = true;
    }

    /// <summary>
    /// Shaking: part of the snow slides off the ends of the window (the nearer end for each half)
    /// and flows down its sides.
    /// </summary>
    public void ShakeOff(float fraction, List<Spill> spill)
    {
        int n = H.Length;
        float leftMass = 0, rightMass = 0, leftH = 0, rightH = 0;
        for (int i = 0; i < n; i++)
        {
            float take = H[i] * fraction;
            if (take <= 0.01f) continue;
            H[i] -= take;
            if (i < n / 2) { leftMass += take * Cw; leftH = MathF.Max(leftH, H[i] + take); }
            else { rightMass += take * Cw; rightH = MathF.Max(rightH, H[i] + take); }
        }
        // flung off the ends of the window as a few soft clumps
        float s = Scale;
        if (leftMass > 0) spill.Add(new Spill { X = Left + 6 * s, Y = Top - leftH, Area = leftMass, Vx = -220f * s + Vx * 0.25f, Vy = -140f * s, Source = Key });
        if (rightMass > 0) spill.Add(new Spill { X = Left + Width - 6 * s, Y = Top - rightH, Area = rightMass, Vx = 220f * s + Vx * 0.25f, Vy = -140f * s, Source = Key });
        _dirty = _relaxNeeded = true;
        _pending += 100;
    }

    /// <summary>
    /// Breaks the snow of columns [c0,c1) into big soft clumps (~50 px of drift each) that drop
    /// from where they lay (window closed/minimized/shrunk).
    /// </summary>
    private void EmitDebris(float[] src, int c0, int c1, float fraction, int left, int top, List<Spill> spill,
                            bool removeFromSelf = false)
    {
        float s = Scale;
        int piece = Math.Max(2, (int)(50f * s / Cw));
        var rng = Random.Shared;
        for (int i = c0; i < c1; i += piece)
        {
            int j = Math.Min(c1, i + piece);
            float area = 0, hMax = 0;
            for (int k = i; k < j; k++)
            {
                float take = src[k] * fraction;
                if (removeFromSelf) src[k] -= take;
                area += take * Cw;
                hMax = MathF.Max(hMax, take);
            }
            if (area < 4f * s * s) continue;
            spill.Add(new Spill
            {
                X = left + (i + j) * 0.5f * Cw, Y = top - hMax * 0.6f, Area = area,
                Vx = Vx * 0.2f + ((float)rng.NextDouble() - 0.5f) * 30f * s, Vy = (float)rng.NextDouble() * 40f * s,
                Source = Key,
            });
        }
        if (removeFromSelf) { _dirty = _relaxNeeded = true; _pending += 100; }
    }

    /// <summary>Debug log hook (set when SUGROB_PROFILE=1).</summary>
    public static Action<string>? Log;

    /// <summary>Everything falls down (window closed or minimized).</summary>
    public void SpillAll(List<Spill> spill)
    {
        Log?.Invoke($"spillAll key={Key} kind={Kind} max={CurMax:F1}");
        if (CurMax > 0.3f) EmitDebris(H, 0, H.Length, 1f, Left, Top, spill, removeFromSelf: true);
    }

    /// <summary>Minimum time between shape rebuilds (seconds). Lowered while the user is sweeping snow.</summary>
    public static double RebuildInterval = 0.25;
    private long _builtEpoch = -1;

    /// <summary>
    /// Returns the drift shape (local coords: x 0..Width, y -h..0) as a geometry realization:
    /// tessellated once and kept on the GPU, so drawing it every frame is nearly free.
    /// </summary>
    public ID2D1GeometryRealization? GetShape(Gpu gpu, double now)
    {
        if (CurMax < 0.4f) return null;
        // All drifts rebuild on the same "tick" (RebuildInterval), so the drift layer is redrawn
        // a few times per second at most instead of whenever any single drift changes.
        long epoch = (long)(now / RebuildInterval);
        if (_geo != null)
        {
            if (epoch == _builtEpoch) return _geo;
            // Rebuild only when the change is visible (~half a pixel), or lazily once in a while.
            bool visible = _pending > 0.5f * Scale;
            bool lazy = _dirty && now - _builtAt >= 2.0;
            if (!visible && !lazy) return _geo;
        }
        _pending = 0;
        _builtEpoch = epoch;

        _geo?.Dispose();
        using var path = gpu.D2DFactory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(0, 0), FigureBegin.Filled);
            int n = H.Length;
            for (int i = 0; i < n; i++)
            {
                float a2 = H[Math.Max(0, i - 2)], a = H[Math.Max(0, i - 1)], b = H[i],
                      c = H[Math.Min(n - 1, i + 1)], c2 = H[Math.Min(n - 1, i + 2)];
                float h = (a2 + 4 * a + 6 * b + 4 * c + c2) * (1f / 16f); // smoothing for display
                sink.AddLine(new Vector2(MathF.Min((i + 0.5f) * Cw, Width), -h));
            }
            sink.AddLine(new Vector2(Width, 0));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        _geo = gpu.Ctx.CreateFilledGeometryRealization(path, 0.25f);
        Version++;
        _builtAt = now;
        _dirty = false;
        return _geo;
    }

    /// <summary>Forces the next GetShape call to rebuild (used after big programmatic changes).</summary>
    public void Invalidate() { _dirty = _relaxNeeded = true; _pending += 100; }

    public void Dispose() { _geo?.Dispose(); _geo = null; }
}

/// <summary>Visible part of a drift's edge on one monitor (monitor-local coords).</summary>
internal struct Seg
{
    public float X0, X1;    // visible horizontal range
    public float Top;       // y of the edge
    public float OriginX;   // local x of the drift's column 0
    public Drift Drift;
}
