using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Sugrob;

/// <summary>Per-frame list of atlas sprites, uploaded to the GPU sprite batch in one go.</summary>
internal sealed class SpriteBuffer
{
    private RawRectF[] _dst = new RawRectF[512];
    private RawRect[] _src = new RawRect[512];
    private Color4[] _col = new Color4[512];
    private Matrix3x2[] _xf = new Matrix3x2[512];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Add(float halfW, float halfH, in RawRect src, in Color4 color, in Matrix3x2 xf)
    {
        if (Count == _dst.Length)
        {
            int cap = _dst.Length * 2;
            Array.Resize(ref _dst, cap); Array.Resize(ref _src, cap);
            Array.Resize(ref _col, cap); Array.Resize(ref _xf, cap);
        }
        int i = Count++;
        _dst[i] = new RawRectF(-halfW, -halfH, halfW, halfH);
        _src[i] = src;
        _col[i] = color;
        _xf[i] = xf;
    }

    /// <summary>Centered sprite without rotation.</summary>
    public void Add(float x, float y, float size, in RawRect src, in Color4 color) =>
        Add(size * 0.5f, size * 0.5f, src, color, Matrix3x2.CreateTranslation(x, y));

    public void Upload(ID2D1SpriteBatch batch)
    {
        batch.Clear();
        if (Count > 0) batch.AddSprites((uint)Count, _dst, _src, _col, _xf, 16, 16, 16, 24);
    }
}
