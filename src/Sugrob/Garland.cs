using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Sugrob;

/// <summary>How the lights glow. (Numbers kept stable for saved settings.)</summary>
public enum GarlandMode { Off = 0, Blink = 1, Wave = 2, Steady = 3, Twinkle = 4, Rainbow = 5, Chase = 6 }

/// <summary>What the lights look like.</summary>
public enum GarlandShape { Classic = 0, Round = 1, Star = 2, Icicle = 3, Retro = 4 }

/// <summary>
/// Christmas lights along the top edges of windows and the taskbar.
/// The wire is static (drawn into the drift layer); the bulbs animate (drawn with the flakes).
/// Bulb positions are relative to the window, so the garland moves with it.
/// </summary>
internal static class Garland
{
    private static readonly Color4[] Colors =
    {
        new(1.00f, 0.25f, 0.25f, 1), // red
        new(0.30f, 0.95f, 0.40f, 1), // green
        new(1.00f, 0.80f, 0.20f, 1), // gold
        new(0.35f, 0.60f, 1.00f, 1), // blue
        new(1.00f, 0.45f, 0.85f, 1), // pink
    };

    private static readonly Color4 Ice = new(0.78f, 0.92f, 1f, 1);
    private static readonly Color4 Warm = new(1f, 0.78f, 0.45f, 1);

    public static GarlandShape Shape = GarlandShape.Classic;
    /// <summary>Bulb size multiplier (from the size setting).</summary>
    public static float SizeK = 1f;

    private static float HookSpan(float s) => 130f * s;

    private static float Spacing(float s) => MathF.Pow(SizeK, 0.75f) * Shape switch
    {
        GarlandShape.Icicle => 13f * s,
        GarlandShape.Retro => 34f * s,
        GarlandShape.Star => 30f * s,
        _ => 26f * s,
    };

    private static bool Applies(in Seg seg) => seg.Drift.Kind != DriftKind.Ground;

    /// <summary>Wire height (relative to the edge) at drift-local x: hooks at the edge, sagging between them.</summary>
    private static float WireY(float lx, float s)
    {
        float span = HookSpan(s);
        float u = (lx % span + span) % span / span;
        return (-1f + 6f * 4f * u * (1f - u)) * s;
    }

    /// <summary>Draws the static wire for every visible window edge (drift layer).</summary>
    public static void DrawWires(ID2D1DeviceContext3 ctx, Gpu gpu, List<Seg> segs)
    {
        foreach (var seg in segs)
        {
            if (!Applies(seg)) continue;
            float s = seg.Drift.Scale;
            float step = 6f * s;
            float x = seg.X0;
            var prev = new Vector2(x, seg.Top + WireY(x - seg.OriginX, s));
            while (x < seg.X1)
            {
                x = MathF.Min(x + step, seg.X1);
                var p = new Vector2(x, seg.Top + WireY(x - seg.OriginX, s));
                ctx.DrawLine(prev, p, gpu.WireBrush, MathF.Max(1f, 1.3f * s));
                prev = p;
            }
        }
    }

    /// <summary>Adds the animated bulbs to the frame's sprites (flake layer).</summary>
    public static void AddBulbs(SpriteBuffer sb, Gpu gpu, List<Seg> segs, GarlandMode mode, float t)
    {
        var glow = gpu.SpriteRects[0];
        var core = gpu.SpriteRects[1];
        var star = gpu.SpriteRects[4];
        foreach (var seg in segs)
        {
            if (!Applies(seg)) continue;
            float s = seg.Drift.Scale;
            float sp = Spacing(s);
            int i0 = (int)MathF.Ceiling((seg.X0 - seg.OriginX) / sp - 0.5f);
            int i1 = (int)MathF.Floor((seg.X1 - seg.OriginX) / sp - 0.5f);
            for (int i = i0; i <= i1; i++)
            {
                float lx = (i + 0.5f) * sp;
                float x = seg.OriginX + lx;
                float y = seg.Top + WireY(lx, s) + 4.5f * s;
                float z = s * SizeK; // bulb size (user setting) — the wire itself keeps its shape
                var c = ColorOf(mode, i, t);
                float b = Brightness(mode, i, t);
                float g = 0.85f * b;
                float k = 0.4f + 0.6f * b, w = 0.4f * b;
                var lit = new Color4(MathF.Min(1, (c.R + w) * k), MathF.Min(1, (c.G + w) * k), MathF.Min(1, (c.B + w) * k), 1f);
                var halo = new Color4(c.R * g, c.G * g, c.B * g, g * 0.3f); // nearly additive glow
                var socket = new Color4(0.12f, 0.16f, 0.12f, 1f);

                switch (Shape)
                {
                    case GarlandShape.Round:
                        sb.Add(x, y + 1f * z, 30f * z, glow, halo);
                        sb.Add(x, y + 0.5f * z, 6.5f * z, core, lit);
                        break;
                    case GarlandShape.Star:
                        sb.Add(x, y + 2f * z, 34f * z, glow, halo);
                        sb.Add(7.5f * z, 7.5f * z, star, lit,
                            Matrix3x2.CreateRotation(MathF.Sin(t * 0.8f + i) * 0.25f) * Matrix3x2.CreateTranslation(x, y + 3f * z));
                        break;
                    case GarlandShape.Icicle:
                    {
                        // dangling LED drops of different lengths, cold white
                        float len = (3.5f + (i * 7 % 5) * 1.3f) * s;
                        var ic = mode == GarlandMode.Rainbow ? lit : new Color4(MathF.Min(1, (Ice.R + w) * k), MathF.Min(1, (Ice.G + w) * k), MathF.Min(1, (Ice.B + w) * k), 1);
                        var ih = mode == GarlandMode.Rainbow ? halo : new Color4(Ice.R * g, Ice.G * g, Ice.B * g, g * 0.3f);
                        sb.Add(6f * z, len + 6f * z, glow, ih, Matrix3x2.CreateTranslation(x, y + len * 0.6f));
                        sb.Add(1.3f * z, len * 0.5f + 1f * z, core, ic, Matrix3x2.CreateTranslation(x, y + len * 0.5f));
                        break;
                    }
                    case GarlandShape.Retro:
                    {
                        // big old-fashioned C9 bulbs with a warm cast
                        var rc = new Color4(MathF.Min(1, lit.R * 0.85f + Warm.R * 0.15f), MathF.Min(1, lit.G * 0.85f + Warm.G * 0.15f), MathF.Min(1, lit.B * 0.85f + Warm.B * 0.15f), 1);
                        sb.Add(x, y + 4f * z, 46f * z, glow, halo);
                        sb.Add(3f * z, 2.4f * z, core, socket, Matrix3x2.CreateTranslation(x, y - 3.8f * z));
                        sb.Add(5.2f * z, 7.6f * z, core, rc, Matrix3x2.CreateTranslation(x, y + 4.2f * z));
                        break;
                    }
                    default: // Classic
                        sb.Add(x, y + 1f * z, 36f * z, glow, halo);
                        sb.Add(2.2f * z, 1.6f * z, core, socket, Matrix3x2.CreateTranslation(x, y - 4.2f * z));
                        sb.Add(4.2f * z, 5.6f * z, core, lit, Matrix3x2.CreateTranslation(x, y + 1f * z));
                        break;
                }
            }
        }
    }

    private static Color4 ColorOf(GarlandMode mode, int i, float t)
    {
        if (mode == GarlandMode.Rainbow)
            return Hsv((t * 0.12f + i * 0.06f) % 1f);
        return Colors[((i % Colors.Length) + Colors.Length) % Colors.Length];
    }

    private static Color4 Hsv(float h)
    {
        float r = Math.Clamp(MathF.Abs(h * 6 - 3) - 1, 0, 1);
        float g = Math.Clamp(2 - MathF.Abs(h * 6 - 2), 0, 1);
        float b = Math.Clamp(2 - MathF.Abs(h * 6 - 4), 0, 1);
        return new Color4(0.25f + 0.75f * r, 0.25f + 0.75f * g, 0.25f + 0.75f * b, 1);
    }

    private static float Hash(int i) { uint h = (uint)i * 2654435761u; return (h >> 8 & 0xFFFF) / 65535f; }

    private static float Brightness(GarlandMode mode, int i, float t)
    {
        switch (mode)
        {
            case GarlandMode.Steady:
                return 1f;
            case GarlandMode.Rainbow:
                return 0.9f;
            case GarlandMode.Wave:
            {
                float v = 0.5f + 0.5f * MathF.Sin(t * 4f - i * 0.55f);
                return v * v;
            }
            case GarlandMode.Twinkle:
            {
                // each bulb sparkles on its own rhythm
                float f = 1.2f + 2.2f * Hash(i), ph = Hash(i + 999) * 6.28f;
                float v = MathF.Max(0, MathF.Sin(t * f + ph));
                return 0.18f + 0.82f * v * v * v;
            }
            case GarlandMode.Chase:
            {
                // bright runners with a fading tail, one every 12 bulbs
                float head = (t * 9f - i) % 12f;
                if (head < 0) head += 12f;
                return 0.12f + 0.88f * MathF.Max(0, 1f - head / 3.5f);
            }
            default: // Blink: two interleaved groups, with a soft fade between states
            {
                float phase = (t * 0.9f + (i & 1) * 0.5f) % 1f;
                float on = phase < 0.5f ? 1f : 0f;
                float edge = MathF.Min(MathF.Abs(phase - 0.5f), MathF.Min(phase, 1f - phase));
                float fade = Math.Clamp(edge * 12f, 0f, 1f);
                return 0.12f + 0.88f * (on * fade + (1 - fade) * 0.5f);
            }
        }
    }
}
