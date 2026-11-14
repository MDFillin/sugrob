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

    private readonly Layer _drifts;
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

    public Overlay(Gpu gpu, RECT bounds, float scale)
    {
        Bounds = bounds;
        Scale = scale;
        EnsureClass();

        Hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "Sugrob", WS_POPUP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        SetLayeredWindowAttributes(Hwnd, 0, 255, LWA_ALPHA);

        _drifts = new Layer(gpu, bounds);
        _flakes = new Layer(gpu, bounds);

        // Clear the drift layer once so it starts transparent.
        Begin(gpu, _drifts);
        End(gpu, _drifts);

        gpu.Comp.CreateTargetForHwnd(Hwnd, true, out _compTarget).CheckError();
        _root = gpu.Comp.CreateVisual();
        _root.SetContent(_drifts.Swap);
        _flakeVisual = gpu.Comp.CreateVisual();
        _flakeVisual.SetContent(_flakes.Swap);
        _root.AddVisual(_flakeVisual, true, null); // children draw above the parent's content
        _compTarget.SetRoot(_root);
        gpu.Comp.Commit();

        Snow = new Snowfall(bounds.Width, bounds.Height, scale);
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
    }

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
        scene.Clumps.Render(gpu, this, scene.Time, Quiet ? _visible : null);
        _sprites.Clear();
        if (scene.Garland != GarlandMode.Off) Garland.AddBulbs(_sprites, gpu, Segs, scene.Garland, scene.Time);
        _visible ??= IsVisibleAt;
        Snow.Fill(_sprites, gpu.SpriteRects, Quiet ? _visible : null, ShowFlakes);
        _sprites.Upload(gpu.Batch);
        if (_sprites.Count > 0)
            gpu.Ctx.DrawSpriteBatch(gpu.Batch, 0, (uint)_sprites.Count, gpu.Atlas, Vortice.Direct2D1.BitmapInterpolationMode.Linear, SpriteOptions.None);
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
        int drawn = 0;
        foreach (var seg in Segs)
        {
            var d = seg.Drift;
            bool wire = garland != GarlandMode.Off && d.Kind != DriftKind.Ground;
            bool shape = d.GetShape(gpu, now) is not null;
            if (!shape && !wire) continue;
            hash.Add(seg.X0); hash.Add(seg.X1); hash.Add(seg.Top); hash.Add(seg.OriginX);
            hash.Add(d.Key); hash.Add(shape ? d.Version : -1);
            drawn++;
        }
        int sig = hash.ToHashCode();
        if (drawn == 0)
        {
            if (_driftLayerEmpty) return;
            Begin(gpu, _drifts);
            End(gpu, _drifts);
            _driftLayerEmpty = true;
            _driftSignature = int.MinValue;
            return;
        }
        if (sig == _driftSignature && !_driftLayerEmpty) return;
        _driftSignature = sig;
        _driftLayerEmpty = false;

        var ctx = gpu.Ctx;
        Begin(gpu, _drifts);
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var seg in Segs)
        {
            var d = seg.Drift;
            var shape = d.GetShape(gpu, now);
            if (shape is null) continue;
            float s = d.Scale;
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(seg.X0, seg.Top - d.MaxH - 4f * s, seg.X1, seg.Top + 3f * s), AntialiasMode.Aliased);
            ctx.Transform = Matrix3x2.CreateTranslation(seg.OriginX, seg.Top + 1.5f * s);
            ctx.DrawGeometryRealization(shape, gpu.ShadowBrush);
            ctx.Transform = Matrix3x2.CreateTranslation(seg.OriginX, seg.Top);
            gpu.DriftBrush.StartPoint = new Vector2(0, -MathF.Max(d.CurMax, 4f * s));
            gpu.DriftBrush.EndPoint = Vector2.Zero;
            ctx.DrawGeometryRealization(shape, gpu.DriftBrush);
            ctx.Transform = Matrix3x2.Identity;
            ctx.PopAxisAlignedClip();
        }
        if (garland != GarlandMode.Off) Garland.DrawWires(ctx, gpu, Segs);
        ctx.AntialiasMode = AntialiasMode.Aliased; // sprite batches require aliased mode
        End(gpu, _drifts);
    }

    public void Dispose()
    {
        DestroyWindow(Hwnd);
        _compTarget.Dispose();
        _flakeVisual.Dispose();
        _root.Dispose();
        _drifts.Dispose();
        _flakes.Dispose();
    }
}
