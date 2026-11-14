using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Drawing = System.Drawing;

namespace Sugrob;

/// <summary>
/// New Year mode: a countdown during the last minute of 31 December and fireworks at midnight.
/// Fireworks live in virtual-desktop coordinates, so rockets may fly on any monitor.
/// </summary>
internal sealed class NewYear : IDisposable
{
    private struct Spark
    {
        public Vector2 P, V;
        public float Life, Max, Size, Drag, Twinkle;
        public Color4 C;
        public bool Rocket;  // still climbing; bursts when it slows down
        public int Kind;     // burst kind for rockets
    }

    private Spark[] _sp = new Spark[1024];
    private int _count;
    private readonly Random _rnd = new();

    private double _testStart = -1;   // manual "show me" run (seconds on the app clock)
    private double _launchAcc;
    private double _midnightTitle = -1;

    /// <summary>Something is on screen (or about to be): keep the render loop awake.</summary>
    public bool Active { get; private set; }

    public void StartTest(double now)
    {
        _testStart = now;
        _midnightTitle = -1;
    }

    // ---------------------------------------------------------------- schedule ---
    private enum Phase { None, Timer, Countdown, Fireworks }

    private Phase _phase;
    private float _secondsLeft;     // before midnight
    private float _sinceMidnight;   // after midnight
    private int _year;

    private const float TestCountdown = 10f, TestShow = 40f, RealShow = 150f;

    private void Schedule(bool enabled, double now)
    {
        _phase = Phase.None;
        if (_testStart >= 0)
        {
            float t = (float)(now - _testStart);
            if (t < TestCountdown) { _phase = Phase.Countdown; _secondsLeft = TestCountdown - t; }
            else if (t < TestCountdown + TestShow) { _phase = Phase.Fireworks; _sinceMidnight = t - TestCountdown; }
            else _testStart = -1;
            _year = DateTime.Now.Year + (DateTime.Now.Month >= 7 ? 1 : 0); // the coming year
            if (_phase != Phase.None) return;
        }
        if (!enabled) return;

        var n = DateTime.Now;
        if (n.Month == 12 && n.Day == 31 && n.Hour == 23 && n.Minute == 59)
        {
            _secondsLeft = 60f - (n.Second + n.Millisecond / 1000f);
            _phase = _secondsLeft <= 10f ? Phase.Countdown : Phase.Timer;
            _year = n.Year + 1;
        }
        else if (n.Month == 1 && n.Day == 1 && n.Hour == 0 && n.Minute < 3)
        {
            _sinceMidnight = n.Minute * 60 + n.Second + n.Millisecond / 1000f;
            if (_sinceMidnight < RealShow) { _phase = Phase.Fireworks; _year = n.Year; }
        }
    }

    // ------------------------------------------------------------------ update ---
    public void Update(float dt, double now, bool enabled, IReadOnlyList<Overlay> overlays)
    {
        Schedule(enabled, now);
        if (_phase == Phase.Fireworks && overlays.Count > 0)
        {
            float show = _testStart >= 0 ? TestShow : RealShow;
            // an opening salvo, then a steady show that thins out at the end
            float rate = _sinceMidnight < 4 ? 7f : _sinceMidnight > show - 15 ? 1.4f : 3f;
            _launchAcc += dt * rate * Math.Min(3, overlays.Count);
            while (_launchAcc >= 1) { _launchAcc -= 1; Launch(overlays); }
        }
        else _launchAcc = 0;

        float g = 260f;
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            ref var s = ref _sp[i];
            s.Life -= dt;
            if (s.Rocket)
            {
                s.V.Y += g * 1.6f * dt * s.Size;
                s.P += s.V * dt;
                // a sparkling trail behind the rocket
                if (_rnd.NextSingle() < 0.8f)
                    Emit(s.P, new Vector2(_rnd.NextSingle() * 40 - 20, 30) * s.Size, 0.45f, 2.2f * s.Size, 2.5f, 0.3f, new Color4(1f, 0.85f, 0.6f, 1));
                if (s.V.Y > -40f * s.Size || s.Life <= 0) { Burst(s); s.Life = 0; }
            }
            else
            {
                float k = MathF.Exp(-s.Drag * dt);
                s.V *= k;
                s.V.Y += g * 0.6f * dt;
                s.P += s.V * dt;
            }
            if (s.Life > 0) _sp[w++] = s;
        }
        _count = w;
        Active = _phase != Phase.None || _count > 0;
        if (_phase == Phase.Fireworks && _midnightTitle < 0) _midnightTitle = now;
        if (_phase == Phase.None) _midnightTitle = -1;
    }

    private static readonly Color4[] Palette =
    {
        new(1f, 0.25f, 0.3f, 1), new(1f, 0.75f, 0.2f, 1), new(0.35f, 0.9f, 0.4f, 1), new(0.3f, 0.7f, 1f, 1),
        new(0.8f, 0.4f, 1f, 1), new(1f, 1f, 1f, 1), new(1f, 0.5f, 0.15f, 1), new(0.3f, 1f, 0.9f, 1),
    };

    private void Launch(IReadOnlyList<Overlay> overlays)
    {
        var o = overlays[_rnd.Next(overlays.Count)];
        if (o.Quiet && _testStart < 0) return; // a fullscreen video: don't explode over it
        var b = o.Bounds;
        float s = o.Scale;
        float x = b.Left + b.Width * (0.12f + 0.76f * _rnd.NextSingle());
        float apex = b.Height * (0.45f + 0.35f * _rnd.NextSingle());      // how high it climbs
        float vy = MathF.Sqrt(2f * 260f * 1.6f * s * apex);
        var r = new Spark
        {
            P = new Vector2(x, b.Bottom), V = new Vector2((_rnd.NextSingle() - 0.5f) * 80 * s, -vy),
            Life = 4, Max = 4, Size = s, Rocket = true, Kind = _rnd.Next(5),
            C = Palette[_rnd.Next(Palette.Length)],
        };
        Add(r);
    }

    private void Burst(in Spark r)
    {
        float s = r.Size;
        var c2 = Palette[_rnd.Next(Palette.Length)];
        switch (r.Kind)
        {
            case 0: // peony: a full sphere
            case 1:
                for (int i = 0; i < 160; i++)
                {
                    var d = RandomDir() * (380f + 180f * _rnd.NextSingle()) * s;
                    Emit(r.P, d, 1.9f + 0.7f * _rnd.NextSingle(), 3.4f * s, 1.5f, 0, i % 3 == 0 ? c2 : r.C);
                }
                break;
            case 2: // ring
            {
                float tilt = 0.35f + 0.5f * _rnd.NextSingle(), rot = _rnd.NextSingle() * MathF.PI;
                for (int i = 0; i < 100; i++)
                {
                    float a = i / 100f * MathF.Tau;
                    var d = new Vector2(MathF.Cos(a), MathF.Sin(a) * tilt);
                    d = Vector2.Transform(d, Matrix3x2.CreateRotation(rot)) * 520f * s;
                    Emit(r.P, d, 2f, 3.6f * s, 1.5f, 0, r.C);
                }
                for (int i = 0; i < 40; i++) Emit(r.P, RandomDir() * 160f * s, 1.4f, 3f * s, 1.5f, 0, c2);
                break;
            }
            case 3: // willow: slow golden streamers
                for (int i = 0; i < 140; i++)
                    Emit(r.P, RandomDir() * (260f + 160f * _rnd.NextSingle()) * s, 3.4f + _rnd.NextSingle(), 3f * s, 1.1f, 0.4f, new Color4(1f, 0.78f, 0.35f, 1));
                break;
            default: // crackle: twinkling glitter
                for (int i = 0; i < 170; i++)
                    Emit(r.P, RandomDir() * (260f + 280f * _rnd.NextSingle()) * s, 1.6f + 0.8f * _rnd.NextSingle(), 3f * s, 1.6f, 1f, i % 2 == 0 ? new Color4(1, 1, 0.9f, 1) : r.C);
                break;
        }
    }

    private Vector2 RandomDir()
    {
        // uniform inside a disc looks like a sphere seen from the side
        float a = _rnd.NextSingle() * MathF.Tau, m = MathF.Sqrt(_rnd.NextSingle());
        return new Vector2(MathF.Cos(a) * m, MathF.Sin(a) * m);
    }

    private void Emit(Vector2 p, Vector2 v, float life, float size, float drag, float twinkle, Color4 c) =>
        Add(new Spark { P = p, V = v, Life = life, Max = life, Size = size, Drag = drag, Twinkle = twinkle, C = c });

    private void Add(in Spark s)
    {
        if (_count >= 6000) return;
        if (_count == _sp.Length) Array.Resize(ref _sp, _sp.Length * 2);
        _sp[_count++] = s;
    }

    // ------------------------------------------------------------------ render ---
    public void AddSprites(SpriteBuffer sb, Gpu gpu, Overlay o, float time)
    {
        if (_count == 0) return;
        var glow = gpu.SpriteRects[0];
        var core = gpu.SpriteRects[1];
        var b = o.Bounds;
        float minX = b.Left - 60, maxX = b.Right + 60, minY = b.Top - 60, maxY = b.Bottom + 60;
        for (int i = 0; i < _count; i++)
        {
            ref var s = ref _sp[i];
            if (s.P.X < minX || s.P.X > maxX || s.P.Y < minY || s.P.Y > maxY) continue;
            float x = s.P.X - b.Left, y = s.P.Y - b.Top;
            if (s.Rocket)
            {
                sb.Add(x, y, 10f * s.Size, glow, new Color4(0.5f, 0.4f, 0.25f, 0.15f));
                sb.Add(x, y, 3.5f * s.Size, core, new Color4(1f, 0.95f, 0.85f, 1f));
                continue;
            }
            float a = Math.Clamp(s.Life / s.Max, 0, 1);
            a = a * a * (3 - 2 * a);
            if (s.Twinkle > 0) a *= 1f - s.Twinkle * 0.8f * (0.5f + 0.5f * MathF.Sin(time * 40f + i * 1.7f));
            if (a < 0.02f) continue;
            float hz = s.Size * (7f + 5f * a);
            sb.Add(x, y, hz, glow, new Color4(s.C.R * a, s.C.G * a, s.C.B * a, a * 0.3f)); // near-additive glow
            float w = 0.35f + 0.65f * a;
            sb.Add(x, y, s.Size * 1.3f, core, new Color4((s.C.R * 0.5f + 0.5f) * a * w, (s.C.G * 0.5f + 0.5f) * a * w, (s.C.B * 0.5f + 0.5f) * a * w, a));
        }
    }

    /// <summary>Countdown / greeting on the primary monitor.</summary>
    public void DrawText(Gpu gpu, Overlay o, bool primary, double now)
    {
        if (!primary || _phase == Phase.None || (o.Quiet && _testStart < 0)) return;
        float W = o.Bounds.Width, H = o.Bounds.Height;
        switch (_phase)
        {
            case Phase.Timer:
            {
                int left = (int)MathF.Ceiling(_secondsLeft);
                var t = Text(gpu, $"До Нового года {left / 60}:{left % 60:00}", 30 * o.Scale);
                if (t == null) return;
                Draw(gpu, t.Value, W / 2, 40 * o.Scale + t.Value.H / 2, 1f, 0.9f);
                break;
            }
            case Phase.Countdown:
            {
                int n = (int)MathF.Ceiling(_secondsLeft);
                float frac = n - _secondsLeft;                     // 0 → 1 within the second
                var t = Text(gpu, n.ToString(), H * 0.22f);
                if (t == null) return;
                float k = 1f + 0.35f * MathF.Pow(1 - frac, 3);    // a punch at the start of each second
                float alpha = frac < 0.75f ? 1f : 1f - (frac - 0.75f) / 0.25f;
                Draw(gpu, t.Value, W / 2, H * 0.42f, k, alpha * 0.95f);
                break;
            }
            case Phase.Fireworks:
            {
                float t0 = _midnightTitle < 0 ? 0 : (float)(now - _midnightTitle);
                if (t0 > 9f) return;
                float alpha = t0 < 0.6f ? t0 / 0.6f : t0 > 7f ? 1f - (t0 - 7f) / 2f : 1f;
                var t = Text(gpu, $"С Новым {_year} годом!", H * 0.075f);
                if (t == null) return;
                Draw(gpu, t.Value, W / 2, H * 0.3f, 1f + 0.03f * t0, alpha);
                break;
            }
        }
    }

    private static void Draw(Gpu gpu, (ID2D1Bitmap1 Bmp, float W, float H) t, float cx, float cy, float k, float alpha)
    {
        float w = t.W * k, h = t.H * k;
        gpu.Ctx.DrawBitmap(t.Bmp, new Vortice.RawRectF(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), alpha,
            Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
    }

    // ------------------------------------------------- text → bitmap (GDI+) ---
    private readonly Dictionary<string, (ID2D1Bitmap1 Bmp, float W, float H)> _text = new();
    private Gpu? _textGpu;

    private (ID2D1Bitmap1 Bmp, float W, float H)? Text(Gpu gpu, string s, float px)
    {
        if (_textGpu != gpu) { ClearText(); _textGpu = gpu; }
        string key = s + "|" + (int)px;
        if (_text.TryGetValue(key, out var hit)) return hit;
        if (_text.Count > 80) ClearText();
        try
        {
            var made = Render(gpu, s, px);
            _text[key] = made;
            return made;
        }
        catch { return null; }
    }

    private static (ID2D1Bitmap1, float, float) Render(Gpu gpu, string s, float px)
    {
        using var family = new Drawing.FontFamily("Segoe UI");
        using var path = new Drawing.Drawing2D.GraphicsPath();
        using var fmt = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        path.AddString(s, family, (int)Drawing.FontStyle.Bold, px, new Drawing.PointF(0, 0), fmt);
        var r = path.GetBounds();
        int pad = (int)(px * 0.25f) + 4;
        int w = (int)MathF.Ceiling(r.Width) + pad * 2, h = (int)MathF.Ceiling(r.Height) + pad * 2;
        using var bmp = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            g.TranslateTransform(pad - r.X, pad - r.Y);
            // soft dark halo so the text reads on any background, then a frosty fill
            for (int i = 6; i >= 1; i--)
            {
                using var pen = new Drawing.Pen(Drawing.Color.FromArgb(18, 10, 20, 40), px * 0.035f * i) { LineJoin = Drawing.Drawing2D.LineJoin.Round };
                g.DrawPath(pen, path);
            }
            using var brush = new Drawing.Drawing2D.LinearGradientBrush(new Drawing.RectangleF(r.X, r.Y, r.Width, r.Height + 1),
                Drawing.Color.White, Drawing.Color.FromArgb(255, 200, 225, 255), Drawing.Drawing2D.LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, w, h), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var props = new BitmapProperties1(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None);
            var d2d = gpu.Ctx.CreateBitmap(new SizeI(w, h), data.Scan0, (uint)data.Stride, props);
            return (d2d, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    private void ClearText()
    {
        foreach (var t in _text.Values) t.Bmp.Dispose();
        _text.Clear();
    }

    public void Dispose() => ClearText();
}
