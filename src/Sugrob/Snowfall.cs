using System.Numerics;
using Vortice;
using Vortice.Mathematics;

namespace Sugrob;

public enum Intensity { Light = 0, Normal = 1, Blizzard = 2 }

/// <summary>What falling snowflakes look like.</summary>
public enum FlakeShape { Mixed = 0, Round = 1, Crystal = 2, Star = 3, Fluffy = 4 }

/// <summary>Size steps (numbers kept stable for saved settings).</summary>
public enum FlakeSize { Small = 0, Medium = 1, Large = 2, Tiny = 3, Huge = 4 }

/// <summary>
/// Falling snow for one monitor (monitor-local physical pixels). Flakes land on visible drift
/// edges. Snow knocked off drifts falls as soft clumps, see <see cref="Clumps"/>.
/// </summary>
internal sealed class Snowfall
{
    private struct Flake
    {
        public float X, Y, Z, Size, Fall, Phase, SwayFreq, SwayAmp, Rot, RotSpeed, Alpha, Area;
        public int Sprite;
    }

    private const int MaxFlakes = 6000;

    /// <summary>How much snow one flake adds to a drift, relative to its size.</summary>
    public static float AccumulationRate = 2.5f;
    public static FlakeShape Shape = FlakeShape.Mixed;
    /// <summary>Snow density multiplier (lower on battery / battery saver).</summary>
    public static float DensityK = 1f;
    /// <summary>Extra density for this instance (the small settings preview looks empty otherwise).</summary>
    public float LocalK = 1f;
    public static FlakeSize SizeSetting = FlakeSize.Medium;

    private readonly Random _rng = new();
    private Flake[] _flakes = new Flake[256];
    private int _count;
    private readonly int _width, _height;
    private readonly float _scale; // DPI scale (1.0 at 96 dpi)

    public Snowfall(int width, int height, float scale)
    {
        _width = width; _height = height; _scale = scale;
    }

    public static (float density, float fallMul) Params(Intensity i) => i switch
    {
        Intensity.Light => (110f, 0.85f),
        Intensity.Blizzard => (850f, 1.45f),
        _ => (300f, 1.0f),
    };

    public static float Factor(FlakeSize size) => size switch
    {
        FlakeSize.Tiny => 0.5f, FlakeSize.Small => 0.72f, FlakeSize.Large => 1.4f, FlakeSize.Huge => 1.9f, _ => 1f,
    };

    private static float SizeFactor => Factor(SizeSetting);

    /// <summary>Fills the screen with flakes at random heights (start, un-pause, look change).</summary>
    public void Prefill(Intensity intensity, Vector2 wind)
    {
        int target = TargetCount(intensity);
        _count = 0;
        for (int i = 0; i < target; i++)
        {
            ref var f = ref Add();
            Spawn(ref f, wind, Params(intensity).fallMul);
            f.Y = (float)_rng.NextDouble() * (_height + f.Size) - f.Size;
        }
    }

    private int TargetCount(Intensity intensity)
    {
        float logicalArea = _width * _height / (_scale * _scale);
        int n = (int)(Params(intensity).density * DensityK * LocalK * logicalArea / (1920f * 1080f));
        return Math.Clamp(n, 0, MaxFlakes);
    }

    /// <param name="wind">x: -3..3 (left/right), y: -2..2 (up/down).</param>
    /// <param name="segs">Visible drift edges on this monitor, top of z-order first.</param>
    /// <param name="flakes">false = nobody can see falling snow here (e.g. a maximized video): skip it.</param>
    public void Update(float dt, float time, Intensity intensity, Vector2 wind, List<Seg> segs, bool flakes = true)
    {
        if (!flakes) return;

        var (_, fallMul) = Params(intensity);
        int target = TargetCount(intensity);
        float windPxX = 55f * _scale, windPxY = 28f * _scale;
        float margin = 80f * _scale + MathF.Abs(wind.X) * windPxX * 2f;

        if (_count < target)
        {
            int add = Math.Min(target - _count, Math.Max(1, (int)(target * dt * 0.6f)));
            for (int i = 0; i < add; i++) Spawn(ref Add(), wind, fallMul);
        }

        for (int i = 0; i < _count; i++)
        {
            ref var f = ref _flakes[i];
            float depth = 0.45f + 0.55f * f.Z;
            float vx = wind.X * windPxX * depth + MathF.Sin(time * f.SwayFreq + f.Phase) * f.SwayAmp;
            float vy = f.Fall * fallMul + wind.Y * windPxY * (0.5f + 0.5f * f.Z);
            vy = MathF.Max(vy, f.Fall * 0.15f); // snow always ends up falling
            float prevY = f.Y;
            f.X += vx * dt;
            f.Y += vy * dt;
            f.Rot += f.RotSpeed * dt;

            bool landed = Land(f.X, prevY, f.Y, f.Area, segs);
            if (landed || f.Y - f.Size > _height || f.X < -margin || f.X > _width + margin)
            {
                if (_count > target)
                {
                    _flakes[i] = _flakes[--_count];
                    i--;
                    continue;
                }
                Spawn(ref f, wind, fallMul);
            }
        }
    }

    private static bool Land(float x, float prevY, float y, float area, List<Seg> segs)
    {
        for (int s = 0; s < segs.Count; s++)
        {
            var seg = segs[s];
            if (x < seg.X0 || x >= seg.X1) continue;
            var d = seg.Drift;
            int col = (int)((x - seg.OriginX) / d.Cw);
            if ((uint)col >= (uint)d.H.Length) continue;
            float surface = seg.Top - d.H[col];
            if (prevY < surface && y >= surface)
            {
                d.Add(col, area);
                return true;
            }
        }
        return false;
    }

    private ref Flake Add()
    {
        if (_count == _flakes.Length) Array.Resize(ref _flakes, _flakes.Length * 2);
        return ref _flakes[_count++];
    }

    private void Spawn(ref Flake f, Vector2 wind, float fallMul)
    {
        float s = _scale;
        float z = MathF.Pow((float)_rng.NextDouble(), 1.6f); // more far flakes than near ones
        f.Z = z;
        f.Sprite = PickSprite(z);
        float shapeMul = f.Sprite switch { 2 or 3 or 4 => 1.35f, 5 => 1.5f, _ => 1f };
        f.Size = (3f + 13f * z * z) * s * shapeMul * SizeFactor;
        f.Fall = (35f + 95f * z) * s * (0.85f + 0.3f * (float)_rng.NextDouble());
        f.Alpha = 0.5f + 0.45f * z;
        f.SwayAmp = (8f + 22f * z) * s * (0.5f + (float)_rng.NextDouble());
        f.SwayFreq = 0.6f + (float)_rng.NextDouble();
        f.Phase = (float)_rng.NextDouble() * MathF.Tau;
        f.Rot = (float)_rng.NextDouble() * MathF.Tau;
        f.RotSpeed = f.Sprite >= 2 ? ((float)_rng.NextDouble() * 2f - 1f) * 1.2f : 0f;
        f.Area = AccumulationRate * (3f + 9f * z) * s * s;

        // horizontal drift during the whole fall, so the screen stays evenly covered in wind
        float drift = wind.X * 55f * s * (0.45f + 0.55f * z) / MathF.Max(1f, f.Fall * fallMul) * _height;
        float minX = MathF.Min(0, -drift), maxX = _width + MathF.Max(0, -drift);
        f.X = minX + (float)_rng.NextDouble() * (maxX - minX);
        f.Y = -f.Size - (float)_rng.NextDouble() * 40f * s;
    }

    private int PickSprite(float z)
    {
        // far flakes are always soft dots: details don't read at 2-3 px anyway
        switch (Shape)
        {
            case FlakeShape.Round: return z < 0.35f ? 0 : 1;
            case FlakeShape.Crystal: return z < 0.2f ? 0 : 2 + _rng.Next(2);
            case FlakeShape.Star: return z < 0.2f ? 0 : 4;
            case FlakeShape.Fluffy: return z < 0.2f ? 0 : 5;
            default: return z < 0.35f ? 0 : z < 0.72f ? 1 : 2 + _rng.Next(2);
        }
    }

    /// <summary>Appends falling flakes to the frame's sprite list.</summary>
    /// <param name="visible">Optional filter (local coords): only draw where it returns true.</param>
    public void Fill(SpriteBuffer sb, RawRect[] rects, Func<float, float, bool>? visible, bool flakes)
    {
        if (!flakes) return;
        {
            for (int i = 0; i < _count; i++)
            {
                ref var f = ref _flakes[i];
                if (visible != null && !visible(f.X, f.Y)) continue;
                float h = f.Size * 0.5f, a = f.Alpha;
                var xf = f.RotSpeed != 0
                    ? Matrix3x2.CreateRotation(f.Rot) * Matrix3x2.CreateTranslation(f.X, f.Y)
                    : Matrix3x2.CreateTranslation(f.X, f.Y);
                sb.Add(h, h, rects[f.Sprite], new Color4(a, a, a, a) /* premultiplied fade */, xf);
            }
        }
    }
}
