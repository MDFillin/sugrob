using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Sugrob.Native;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Sugrob;

public enum WallpaperKind { Off = 0, Cabin = 1, Porch = 2 }

/// <summary>
/// Live wallpaper: windows placed between the wallpaper and the desktop icons.
/// The user's own wallpaper is never changed — we only cover it while the scene is on,
/// and repaint the desktop when it's turned off.
///
/// Windows 10 and Windows 11 before 24H2: icons live in a top-level WorkerW; after message
/// 0x052C Explorer creates a second WorkerW behind it, which we parent our windows to.
/// Windows 11 24H2+: icons (SHELLDLL_DefView) are a child of Progman; we become a child of
/// Progman placed right below the icon view.
/// </summary>
internal sealed class WallpaperHost : IDisposable
{
    private const string ClassName = "Sugrob.Wallpaper";
    private static WndProc? _proc;
    private static bool _registered;

    private sealed class Screen : IDisposable
    {
        public IntPtr Hwnd;
        public RECT Mon;
        public float Scale;
        public IDXGISwapChain1 Swap = null!;
        public ID2D1Bitmap1 Target = null!;
        public ID2D1Bitmap1? Static;
        public IDCompositionTarget? CompTarget;
        public IDCompositionVisual? Visual;
        public SceneArt Art = null!;
        public bool Covered;

        public void Dispose()
        {
            Art?.Dispose();
            Static?.Dispose();
            Target?.Dispose();
            Visual?.Dispose();
            CompTarget?.Dispose();
            Swap?.Dispose();
            if (Hwnd != IntPtr.Zero) DestroyWindow(Hwnd);
        }
    }

    private readonly List<Screen> _screens = new();
    private IntPtr _parent;
    private bool _modern;      // Windows 11 24H2+ layout (child of Progman)
    private IntPtr _defView;   // icons view (modern layout: we go right below it)

    public bool Active => _screens.Count > 0;
    public string? LastError { get; private set; }

    // ------------------------------------------------------------------ setup ------
    private bool FindParent()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { LastError = "Progman not found"; return false; }
        // ask Explorer to create the WorkerW that sits behind the icons
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        _defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (_defView != IntPtr.Zero)
        {
            _modern = true;
            _parent = progman;
            return true;
        }

        // classic: find the top-level window holding the icons, the WorkerW right after it is ours
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
        {
            if (FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                _parent = FindWindowEx(IntPtr.Zero, w, "WorkerW", null);
                _modern = false;
                return _parent != IntPtr.Zero;
            }
        }
        LastError = "WorkerW not found";
        return false;
    }

    public void Build(Gpu gpu, IReadOnlyList<Overlay> overlays, WallpaperKind kind)
    {
        Teardown(repaint: false);
        if (kind == WallpaperKind.Off) { RepaintDesktop(); return; }
        if (!FindParent()) return;
        EnsureClass();

        foreach (var o in overlays)
        {
            var sc = new Screen { Mon = o.Bounds, Scale = o.Scale };
            try
            {
                CreateScreen(gpu, sc, kind);
                _screens.Add(sc);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                sc.Dispose();
            }
        }
    }

    private void CreateScreen(Gpu gpu, Screen sc, WallpaperKind kind)
    {
        var b = sc.Mon;
        sc.Hwnd = CreateWindowEx(0, ClassName, "Sugrob wallpaper", WS_POPUP, b.Left, b.Top, b.Width, b.Height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (sc.Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx");

        // become a child of the desktop window and position in its client coordinates
        SetWindowLongPtr(sc.Hwnd, GWL_STYLE, new IntPtr(unchecked((int)(WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS))));
        if (_modern)
        {
            long ex = (long)GetWindowLongPtr(sc.Hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
            SetWindowLongPtr(sc.Hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetLayeredWindowAttributes(sc.Hwnd, 0, 255, LWA_ALPHA);
        }
        SetParent(sc.Hwnd, _parent);
        var pt = new POINT { X = b.Left, Y = b.Top };
        ScreenToClient(_parent, ref pt);
        SetWindowPos(sc.Hwnd, _modern ? _defView : IntPtr.Zero, pt.X, pt.Y, b.Width, b.Height,
            SWP_NOACTIVATE | (_modern ? 0 : 0x0004u /* NOZORDER */));

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
        sc.Swap = gpu.Factory.CreateSwapChainForComposition(gpu.D3D, desc, null);
        gpu.Comp.CreateTargetForHwnd(sc.Hwnd, false, out sc.CompTarget).CheckError();
        sc.Visual = gpu.Comp.CreateVisual();
        sc.Visual.SetContent(sc.Swap);
        sc.CompTarget!.SetRoot(sc.Visual);
        gpu.Comp.Commit();
        sc.Target = gpu.CreateTargetBitmap(sc.Swap);

        sc.Art = kind == WallpaperKind.Porch ? new PorchScene() : new CabinScene();
        sc.Art.Layout(b.Width, b.Height, sc.Scale);
        sc.Static = RenderStatic(gpu, sc);
        Render(gpu, sc, 0);
    }

    private static ID2D1Bitmap1 RenderStatic(Gpu gpu, Screen sc)
    {
        var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        var bmp = gpu.Ctx.CreateBitmap(new SizeI(sc.Mon.Width, sc.Mon.Height), IntPtr.Zero, 0, props);
        var ctx = gpu.Ctx;
        ctx.Target = bmp;
        ctx.BeginDraw();
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        ctx.Clear(new Color4(0, 0, 0, 1));
        sc.Art.DrawStatic(ctx, gpu);
        ctx.AntialiasMode = AntialiasMode.Aliased;
        ctx.EndDraw();
        ctx.Target = null;
        return bmp;
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        _proc = (h, m, w, l) => m switch
        {
            WM_NCHITTEST => new IntPtr(HTTRANSPARENT),
            WM_ERASEBKGND => new IntPtr(1),
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

    // ------------------------------------------------------------------ frame ------
    private readonly SpriteBuffer _sprites = new();

    public void Render(Gpu gpu, float t, World world)
    {
        foreach (var sc in _screens)
        {
            if (!IsWindow(sc.Hwnd)) continue;             // Explorer restarted; the app rebuilds us
            bool covered = world.IsCovered(sc.Mon);
            if (covered && sc.Covered) continue;           // nobody can see it — don't animate
            sc.Covered = covered;
            Render(gpu, sc, t);
        }
    }

    public bool Broken => _screens.Any(s => !IsWindow(s.Hwnd));

    private void Render(Gpu gpu, Screen sc, float t)
    {
        var ctx = gpu.Ctx;
        ctx.Target = sc.Target;
        ctx.BeginDraw();
        if (sc.Static != null) ctx.DrawBitmap(sc.Static, 1f, Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        sc.Art.DrawDynamic(ctx, gpu, t);
        ctx.AntialiasMode = AntialiasMode.Aliased;
        _sprites.Clear();
        sc.Art.AddSprites(_sprites, gpu, t);
        if (_sprites.Count > 0)
        {
            _sprites.Upload(gpu.Batch);
            ctx.DrawSpriteBatch(gpu.Batch, 0, (uint)_sprites.Count, gpu.Atlas, Vortice.Direct2D1.BitmapInterpolationMode.Linear, SpriteOptions.None);
        }
        ctx.EndDraw();
        ctx.Target = null;
        sc.Swap.Present(0, PresentFlags.None).CheckError();
    }

    // ------------------------------------------------------------------ teardown ---
    private void Teardown(bool repaint)
    {
        foreach (var sc in _screens) sc.Dispose();
        bool had = _screens.Count > 0;
        _screens.Clear();
        if (repaint && had) RepaintDesktop();
    }

    /// <summary>Makes Explorer redraw the user's own wallpaper where our scene was.</summary>
    private void RepaintDesktop()
    {
        if (_parent != IntPtr.Zero && IsWindow(_parent))
            RedrawWindow(_parent, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    public void Dispose() => Teardown(repaint: true);
}
