using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Sugrob;

/// <summary>Loads the user's picture for the custom snowplow (and removes a plain background).</summary>
internal static class ImageLoader
{
    private const int MaxSide = 384;

    public static CustomImage? Load(Gpu gpu, string path)
    {
        try
        {
            var (px, w, h) = Decode(path);
            RemovePlainBackground(px, w, h);
            Premultiply(px);
            unsafe
            {
                fixed (byte* p = px)
                {
                    var props = new BitmapProperties1(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96);
                    var bmp = gpu.Ctx.CreateBitmap(new SizeI(w, h), (IntPtr)p, (uint)(w * 4), props);
                    return new CustomImage(bmp, w / (float)h);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] px, int w, int h) Decode(string path)
    {
        using var wic = new IWICImagingFactory();
        using var decoder = wic.CreateDecoderFromFileName(path);
        using var frame = decoder.GetFrame(0);
        frame.GetSize(out uint w0, out uint h0);

        // scale big photos down — it's a small sprite on screen
        float k = MathF.Min(1f, MaxSide / (float)Math.Max(w0, h0));
        uint w = Math.Max(1u, (uint)(w0 * k)), h = Math.Max(1u, (uint)(h0 * k));

        using var scaler = wic.CreateBitmapScaler();
        scaler.Initialize(frame, w, h, Vortice.WIC.BitmapInterpolationMode.Fant);
        using var conv = wic.CreateFormatConverter();
        conv.Initialize(scaler, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

        var px = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = px) conv.CopyPixels(new RectI(0, 0, (int)w, (int)h), w * 4, (uint)px.Length, (IntPtr)p);
        }
        return (px, (int)w, (int)h);
    }

    /// <summary>
    /// If the picture has no transparency and its border is one plain colour (a typical photo
    /// on a white/flat background or a logo), flood-fills that colour from the edges to transparent.
    /// </summary>
    private static void RemovePlainBackground(byte[] px, int w, int h)
    {
        for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) return; // already has alpha

        // background colour = average of the four corners, which must agree
        (int x, int y)[] corners = { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) };
        int br = 0, bg = 0, bb = 0;
        foreach (var (x, y) in corners) { int i = (y * w + x) * 4; bb += px[i]; bg += px[i + 1]; br += px[i + 2]; }
        br /= 4; bg /= 4; bb /= 4;
        foreach (var (x, y) in corners)
            if (Dist(px, (y * w + x) * 4, br, bg, bb) > 45) return;

        const int tol = 42;
        var removed = new bool[w * h];
        var queue = new Queue<int>();
        void Seed(int x, int y)
        {
            int idx = y * w + x;
            if (!removed[idx] && Dist(px, idx * 4, br, bg, bb) <= tol) { removed[idx] = true; queue.Enqueue(idx); }
        }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int x = idx % w, y = idx / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }
        // a little remaining picture? then it wasn't really a background
        int count = 0;
        foreach (bool r in removed) if (r) count++;
        if (count > w * h * 0.92) return;

        for (int idx = 0; idx < removed.Length; idx++)
        {
            if (removed[idx]) { px[idx * 4 + 3] = 0; continue; }
            // soften the edge next to the removed area
            int x = idx % w, y = idx / w;
            bool edge = (x > 0 && removed[idx - 1]) || (x < w - 1 && removed[idx + 1]) ||
                        (y > 0 && removed[idx - w]) || (y < h - 1 && removed[idx + w]);
            if (edge)
            {
                float d = Dist(px, idx * 4, br, bg, bb);
                px[idx * 4 + 3] = (byte)Math.Clamp((d - tol) / 50f * 255f, 60, 255);
            }
        }
    }

    private static float Dist(byte[] px, int i, int r, int g, int b)
    {
        int db = px[i] - b, dg = px[i + 1] - g, dr = px[i + 2] - r;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static void Premultiply(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            px[i] = (byte)(px[i] * a / 255);
            px[i + 1] = (byte)(px[i + 1] * a / 255);
            px[i + 2] = (byte)(px[i + 2] * a / 255);
        }
    }

    // ---------------------------------------------------------------- file dialog ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner, hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter, nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir, lpstrTitle;
        public int Flags;
        public short nFileOffset, nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData, lpfnHook, lpTemplateName, pvReserved;
        public int dwReserved, FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    /// <summary>Shows "open file" for a picture. Returns null if cancelled.</summary>
    public static string? PickFile(IntPtr owner)
    {
        const int max = 1024;
        IntPtr buf = Marshal.AllocHGlobal(max * 2);
        try
        {
            Marshal.Copy(new byte[max * 2], 0, buf, max * 2);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = owner,
                lpstrFilter = "Картинки (PNG, JPG, BMP, GIF)\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp\0Все файлы\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = buf,
                nMaxFile = max,
                lpstrTitle = "Картинка для снегоуборщика",
                lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Flags = 0x1000 | 0x800 | 0x8 | 0x80000, // FILEMUSTEXIST | PATHMUSTEXIST | NOCHANGEDIR | EXPLORER
            };
            return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buf) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
