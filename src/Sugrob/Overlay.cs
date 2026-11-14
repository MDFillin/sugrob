using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>Everything besides falling snow that overlays draw in a frame.</summary>
internal sealed class Scene
{
    public Snowballs Balls = new();
    public World World = null!;
    public GarlandMode Garland;
    public float Time;
    public readonly List<Snowplow> Plows = new();
    public readonly Clumps Clumps = new();
    public CustomImage? Image;
    public NewYear? NewYear;
}

/// <summary>
/// A transparent, click-through, always-on-top window covering one monitor.
/// It has two DirectComposition layers backed by premultiplied-alpha swap chains:
///   * drifts (bottom) — redrawn only when a drift's shape or a window position changes;
///   * flakes (top)    — redrawn every frame.
/// The GPU compositor blends them directly — no CPU-side layered-window blits.
/// </summary>
internal sealed class Overlay : IDisposable
{
    public const string ClassName = "Sugrob.Overlay";
    private static WndProc? _proc; // keep delegate alive
    private static bool _registered;

    public IntPtr Hwnd { get; }
    public RECT Bounds { get; }
    public float Scale { get; }
    public Snowfall Snow { get; }

    /// <summary>Visible drift edges on this monitor (rebuilt every frame).</summary>
    public List<Seg> Segs { get; } = new();

    // Drifts live on a sparse DirectComposition virtual surface: GPU memory is only allocated
    // for the thin bands where snow actually lies, and only changed bands are redrawn.
    private readonly IDCompositionVirtualSurface _driftSurface;
    private List<Vortice.RawRect> _bands = new(), _newBands = new();
    private readonly Layer _flakes;
    private readonly IDCompositionTarget _compTarget;
    private readonly IDCompositionVisual _root;
    private readonly IDCompositionVisual _flakeVisual;
    private int _driftSignature = int.MinValue;
    private bool _driftLayerEmpty = true;

    // profiling counters (see App.Profile)
    public static long PDrifts, PSprites, PEnd, PPresent;

    private sealed class Layer : IDisposable
    {
        public readonly IDXGISwapChain1 Swap;
        public readonly ID2D1Bitmap1 Target;

        public Layer(Gpu gpu, RECT b)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)b.Width,
                Height = (uint)b.Height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            };
            Swap = gpu.Factory.CreateSwapChainForComposition(gpu.D3D, desc, null);
            Target = gpu.CreateTargetBitmap(Swap);
        }

        public void Dispose() { Target.Dispose(); Swap.Dispose(); }
    }

    private readonly bool _external;

    /// <param name="host">Draw into this existing window (the settings preview) instead of creating an overlay.</param>
    public Overlay(Gpu gpu, RECT bounds, float scale, IntPtr host = default, int width = 0, int height = 0)
    {
        Bounds = bounds;
        Scale = scale;
        _external = host != IntPtr.Zero;
        if (!_external) { width = bounds.Width; height = bounds.Height; }

        if (_external) Hwnd = host;
        else
        {
            EnsureClass();
            Hwnd = CreateWindowEx(
                WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
                ClassName, "Sugrob", WS_POPUP,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            SetLayeredWindowAttributes(Hwnd, 0, 255, LWA_ALPHA);
        }

        var size = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        _flakes = new Layer(gpu, size);
        gpu.Comp.CreateVirtualSurface((uint)width, (uint)height, Format.B8G8R8A8_UNorm,
            Vortice.DXGI.AlphaMode.Premultiplied, out _driftSurface).CheckError();
        _w = width; _h = height;

        gpu.Comp.CreateTargetForHwnd(Hwnd, true, out _compTarget).CheckError();
        _root = gpu.Comp.CreateVisual();
        _root.SetContent(_driftSurface);
        _flakeVisual = gpu.Comp.CreateVisual();
        _flakeVisual.SetContent(_flakes.Swap);
        _root.AddVisual(_flakeVisual, true, null); // children draw above the parent's content
        _compTarget.SetRoot(_root);
        gpu.Comp.Commit();

        Snow = new Snowfall(width, height, scale);
        if (!_external) ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
    }

    private readonly int _w, _h;

    private static void EnsureClass()
    {
        if (_registered) return;
        _proc = (h, m, w, l) => m switch
        {
            WM_NCHITTEST => new IntPtr(Interactive ? HTCLIENT : HTTRANSPARENT),
            WM_MOUSEACTIVATE => new IntPtr(MA_NOACTIVATE),
            WM_SETCURSOR when Interactive => SetHandCursor(),
            _ => DefWindowProc(h, m, w, l),
        };
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _registered = true;
    }

    public void SetVisible(bool visible) => ShowWindow(Hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);

    /// <summary>Sculpt mode: overlays catch the mouse instead of letting clicks through.</summary>
    public static bool Interactive { get; private set; }

    private static IntPtr _hand;
    private static IntPtr SetHandCursor()
    {
        if (_hand == IntPtr.Zero) _hand = LoadCursor(IntPtr.Zero, new IntPtr(IDC_HAND));
        SetCursor(_hand);
        return new IntPtr(1);
    }

    public static void SetInteractive(IEnumerable<Overlay> overlays, bool on)
    {
        Interactive = on;
        foreach (var o in overlays)
        {
            long ex = (long)GetWindowLongPtr(o.Hwnd, GWL_EXSTYLE);
            ex = on ? ex & ~(long)WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
            SetWindowLongPtr(o.Hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(o.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | 0x0020 /* FRAMECHANGED */);
        }
    }

    public void KeepOnTop() =>
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    private static void Begin(Gpu gpu, Layer layer)
    {
        var ctx = gpu.Ctx;
        ctx.Target = layer.Target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
    }

    private static void End(Gpu gpu, Layer layer)
    {
        gpu.Ctx.EndDraw();
        gpu.Ctx.Target = null;
        layer.Swap.Present(0, PresentFlags.None).CheckError();
    }

    private readonly SpriteBuffer _sprites = new();

    /// <summary>A maximized/fullscreen window is on top here: snow only over <see cref="Allowed"/>.</summary>
    public bool Quiet;
    public readonly List<RECT> Allowed = new();
    /// <summary>Windows of excluded programs (monitor-local): never any snow over them.</summary>
    public readonly List<RECT> Blocked = new();
    private bool Filtered => Quiet || Blocked.Count > 0;
    /// <summary>Refresh rate of this monitor (Hz) — used by the "match monitor" frame rate mode.</summary>
    public int RefreshHz { get; init; } = 60;
    /// <summary>When this monitor was last drawn (seconds) — monitors may run at different rates.</summary>
    public double LastRender = -1;
    public int RenderCount;

    public bool ShowFlakes => FlakesAllowed && (!Quiet || Allowed.Count > 0);

    /// <summary>False when Windows animations are turned off (accessibility): no falling snow then.</summary>
    public static bool FlakesAllowed = true;
    private Func<float, float, bool>? _visible;

    private bool IsVisibleAt(float x, float y)
    {
        foreach (var r in Blocked)
            if (x >= r.Left && x < r.Right && y >= r.Top - 40 && y < r.Bottom) return false;
        if (!Quiet) return true;
        foreach (var r in Allowed)
            if (x >= r.Left && x < r.Right && y >= r.Top - 40 && y < r.Bottom) return true;
        return false;
    }

    public void Render(Gpu gpu, double now, Scene scene)
    {
        long t0 = Stopwatch.GetTimestamp();
        RenderDrifts(gpu, now, scene.Garland);
        long t1 = Stopwatch.GetTimestamp();

        Begin(gpu, _flakes);
        scene.Balls.Render(gpu, this);
        foreach (var p in scene.Plows) p.Render(gpu, this, scene.World, scene.Time, scene.Image);
        _visible ??= IsVisibleAt;
        scene.Clumps.Render(gpu, this, scene.Time, Filtered ? _visible : null);
        _sprites.Clear();
        if (scene.Garland != GarlandMode.Off) Garland.AddBulbs(_sprites, gpu, Segs, scene.Garland, scene.Time);
        scene.NewYear?.AddSprites(_sprites, gpu, this, scene.Time);
        _visible ??= IsVisibleAt;
        Snow.Fill(_sprites, gpu.SpriteRects, Filtered ? _visible : null, ShowFlakes);
        _sprites.Upload(gpu.Batch);
        if (_sprites.Count > 0)
            gpu.Ctx.DrawSpriteBatch(gpu.Batch, 0, (uint)_sprites.Count, gpu.Atlas, Vortice.Direct2D1.BitmapInterpolationMode.Linear, SpriteOptions.None);
        scene.NewYear?.DrawText(gpu, this, Bounds.Left <= 0 && Bounds.Top <= 0 && Bounds.Right > 0 && Bounds.Bottom > 0, now);
        long t2 = Stopwatch.GetTimestamp();
        gpu.Ctx.EndDraw();
        gpu.Ctx.Target = null;
        long t3 = Stopwatch.GetTimestamp();
        _flakes.Swap.Present(0, PresentFlags.None).CheckError();
        long t4 = Stopwatch.GetTimestamp();
        PDrifts += t1 - t0; PSprites += t2 - t1; PEnd += t3 - t2; PPresent += t4 - t3;
    }

    /// <summary>Redraws the drift layer only if something visible changed since last time.</summary>
    private void RenderDrifts(Gpu gpu, double now, GarlandMode garland)
    {
        // Signature of everything that affects the picture: visible segments + shape versions.
        var hash = new HashCode();
        hash.Add(garland != GarlandMode.Off);
        _newBands.Clear();
        int w = _w, h = _h;
        foreach (var seg in Segs)
        {
            var d = seg.Drift;
            bool wire = garland != GarlandMode.Off && d.Kind != DriftKind.Ground;
            bool shape = d.GetShape(gpu, now) is not null;
            if (!shape && !wire) continue;
            hash.Add(seg.X0); hash.Add(seg.X1); hash.Add(seg.Top); hash.Add(seg.OriginX);
            hash.Add(d.Key); hash.Add(shape ? d.Version : -1);
            float s = d.Scale;
            var r = new Vortice.RawRect(
                Math.Max(0, (int)MathF.Floor(seg.X0) - 2),
                Math.Max(0, (int)MathF.Floor(seg.Top - d.MaxH - 6f * s)),
                Math.Min(w, (int)MathF.Ceiling(seg.X1) + 2),
                Math.Min(h, (int)MathF.Ceiling(seg.Top + 10f * s)));
            if (r.Right > r.Left && r.Bottom > r.Top) _newBands.Add(r);
        }
        int sig = hash.ToHashCode();
        if (_newBands.Count == 0)
        {
            if (_driftLayerEmpty) return;
            _driftSurface.Trim(Array.Empty<Vortice.RawRect>(), 0); // releases all drift memory
            _bands.Clear();
            _driftLayerEmpty = true;
            _driftSignature = int.MinValue;
            return;
        }
        if (sig == _driftSignature && !_driftLayerEmpty) return;
        _driftSignature = sig;
        _driftLayerEmpty = false;

        // Redraw old ∪ new bands (old ones get cleared), then keep only the new ones.
        _dirty.Clear();
        _dirty.AddRange(_bands);
        _dirty.AddRange(_newBands);
        Merge(_dirty);
        Merge(_newBands);

        var ctx = gpu.Ctx;
        foreach (var rect in _dirty) DrawBand(gpu, ctx, rect, now, garland);
        _driftSurface.Trim(_newBands.ToArray(), (uint)_newBands.Count);
        (_bands, _newBands) = (_newBands, _bands);
    }

    private readonly List<Vortice.RawRect> _dirty = new();

    /// <summary>Unions overlapping or touching rectangles in place (lists here are tiny).</summary>
    private static void Merge(List<Vortice.RawRect> rs)
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < rs.Count && !changed; i++)
                for (int j = i + 1; j < rs.Count; j++)
                {
                    var a = rs[i]; var b = rs[j];
                    if (a.Left > b.Right + 8 || b.Left > a.Right + 8 || a.Top > b.Bottom + 8 || b.Top > a.Bottom + 8) continue;
                    rs[i] = new Vortice.RawRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
                    rs.RemoveAt(j);
                    changed = true;
                    break;
                }
        }
    }


    private void DrawBand(Gpu gpu, ID2D1DeviceContext3 ctx, Vortice.RawRect rect, double now, GarlandMode garland)
    {
        var surface0 = _driftSurface.BeginDraw<IDXGISurface>(rect, out Int2 offset);
        try
        {
            using var surface = surface0;
            var props = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);
            using var bmp = ctx.CreateBitmapFromDxgiSurface(surface, props);
            ctx.Target = bmp;
            ctx.BeginDraw();
            var baseT = Matrix3x2.CreateTranslation(offset.X - rect.Left, offset.Y - rect.Top);
            ctx.Transform = baseT;
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), AntialiasMode.Aliased);
            ctx.Clear(new Color4(0, 0, 0, 0));
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            foreach (var seg in Segs)
            {
                var d = seg.Drift;
                if (seg.X1 < rect.Left || seg.X0 > rect.Right || seg.Top < rect.Top || seg.Top - d.MaxH > rect.Bottom) continue;
                var shape = d.GetShape(gpu, now);
                if (shape is null) continue;
                float s = d.Scale;
                ctx.Transform = baseT;
                ctx.PushAxisAlignedClip(new Vortice.RawRectF(seg.X0, seg.Top - d.MaxH - 4f * s, seg.X1, seg.Top + 3f * s), AntialiasMode.Aliased);
                ctx.Transform = Matrix3x2.CreateTranslation(seg.OriginX, seg.Top + 1.5f * s) * baseT;
                ctx.DrawGeometryRealization(shape, gpu.ShadowBrush);
                ctx.Transform = Matrix3x2.CreateTranslation(seg.OriginX, seg.Top) * baseT;
                gpu.DriftBrush.StartPoint = new Vector2(0, -MathF.Max(d.CurMax, 4f * s));
                gpu.DriftBrush.EndPoint = Vector2.Zero;
                ctx.DrawGeometryRealization(shape, gpu.DriftBrush);
                ctx.Transform = baseT;
                ctx.PopAxisAlignedClip();
            }
            ctx.Transform = baseT;
            if (garland != GarlandMode.Off) Garland.DrawWires(ctx, gpu, Segs);
            ctx.AntialiasMode = AntialiasMode.Aliased; // sprite batches require aliased mode
            ctx.PopAxisAlignedClip();
            ctx.Transform = Matrix3x2.Identity;
            ctx.EndDraw();
            ctx.Target = null;
        }
        finally { _driftSurface.EndDraw(); }
    }

    public void Dispose()
    {
        if (!_external) DestroyWindow(Hwnd);
        _compTarget.Dispose();
        _flakeVisual.Dispose();
        _root.Dispose();
        _driftSurface.Dispose();
        _flakes.Dispose();
    }
}
