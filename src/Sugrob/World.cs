using static Sugrob.Native;

namespace Sugrob;

/// <summary>
/// Tracks top-level windows (z-order and frame bounds) and owns all snowdrifts.
/// Each frame it produces, per monitor, the list of visible drift edges flakes can land on.
/// </summary>
internal sealed class World : IDisposable
{
    private struct Win
    {
        public IntPtr Hwnd;
        public RECT R;
        public bool Surface;
        public bool Taskbar;
        public bool Excluded;
    }

    private readonly List<Win> _z = new();                     // visible windows, top to bottom
    private readonly Dictionary<IntPtr, Drift> _drifts = new();
    private readonly Dictionary<IntPtr, string> _classCache = new();
    private readonly List<IntPtr> _gone = new();
    private readonly List<(float a, float b)> _iv = new(), _ivTmp = new();
    private readonly char[] _clsBuf = new char[256];
    private readonly EnumWindowsProc _enumProc;
    private IReadOnlyList<Overlay> _overlays = Array.Empty<Overlay>();
    private double _classCacheAge;

    /// <summary>Snow that fell off drifts this frame (virtual coords). Consumed by the app.</summary>
    public List<Spill> Spill { get; } = new();

    // Window changes are reported by WinEvent hooks; we rescan only after an event
    // (plus a safety rescan every second), so an idle desktop costs almost nothing.
    private readonly WinEventProc _eventProc;
    private readonly List<IntPtr> _hooks = new();
    private bool _eventsDirty = true;
    private double _sinceScan;
    private bool _overlaysChanged = true;

    public World()
    {
        _enumProc = OnWindow;
        _eventProc = OnWinEvent;
        (uint, uint)[] ranges =
        {
            (0x0003, 0x0003), // SYSTEM_FOREGROUND
            (0x000A, 0x000B), // SYSTEM_MOVESIZESTART/END
            (0x0016, 0x0017), // SYSTEM_MINIMIZESTART/END
            (0x8001, 0x8004), // OBJECT_DESTROY/SHOW/HIDE/REORDER
            (0x800B, 0x800B), // OBJECT_LOCATIONCHANGE
            (0x8017, 0x8018), // OBJECT_CLOAKED/UNCLOAKED
        };
        foreach (var (a, b) in ranges)
        {
            var h = SetWinEventHook(a, b, IntPtr.Zero, _eventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // only whole windows (not carets, cursors, scrollbars...)
        if (idObject == 0 && idChild == 0) _eventsDirty = true;
    }

    public IEnumerable<Drift> Drifts => _drifts.Values;

    public Drift? GetDrift(IntPtr key) => _drifts.TryGetValue(key, out var d) ? d : null;

    /// <summary>
    /// "Don't disturb" check: is a maximized/fullscreen window (video, browser...) shown on this monitor?
    /// If so, returns true and fills <paramref name="allowed"/> with the rects (monitor-local) of the
    /// windowed apps lying above it — snow may still fall over those, but not over the big window.
    /// </summary>
    public bool QuietRegion(RECT mon, List<RECT> allowed)
    {
        allowed.Clear();
        long area = (long)mon.Width * mon.Height;
        foreach (var w in _z)
        {
            if (w.Taskbar) continue;
            var r = w.R;
            long ix = Math.Max(0, Math.Min(r.Right, mon.Right) - Math.Max(r.Left, mon.Left));
            long iy = Math.Max(0, Math.Min(r.Bottom, mon.Bottom) - Math.Max(r.Top, mon.Top));
            if (ix * iy == 0) continue;
            if (ix * iy >= area * 0.8) return true; // the big window: everything below it is hidden anyway
            if (w.Surface)
                allowed.Add(new RECT { Left = r.Left - mon.Left, Top = r.Top - mon.Top, Right = r.Right - mon.Left, Bottom = r.Bottom - mon.Top });
        }
        allowed.Clear();
        return false;
    }

    /// <summary>True if visible windows hide (almost) the whole monitor — no point animating the wallpaper.</summary>
    public bool IsCovered(RECT mon)
    {
        long area = (long)mon.Width * mon.Height;
        foreach (var w in _z)
        {
            if (w.Taskbar) continue;
            var r = w.R;
            long ix = Math.Max(0, Math.Min(r.Right, mon.Right) - Math.Max(r.Left, mon.Left));
            long iy = Math.Max(0, Math.Min(r.Bottom, mon.Bottom) - Math.Max(r.Top, mon.Top));
            if (ix * iy >= area * 0.9) return true;
        }
        return false;
    }

    // ------------------------------------------------------------- app exclusions ---
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _procCache = new();
    private readonly char[] _pathBuf = new char[1024];

    /// <summary>Programs (exe file names) over which snow is never shown and nothing piles up.</summary>
    public void SetExcluded(IEnumerable<string> exeNames)
    {
        _excluded.Clear();
        foreach (var n in exeNames)
        {
            var s = n.Trim();
            if (s.Length == 0) continue;
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            _excluded.Add(s);
        }
        _eventsDirty = true;
    }

    private bool IsExcluded(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == 0) return false;
        if (!_procCache.TryGetValue(pid, out var exe))
        {
            exe = "";
            var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p != IntPtr.Zero)
            {
                uint size = (uint)_pathBuf.Length;
                if (QueryFullProcessImageName(p, 0, _pathBuf, ref size))
                    exe = Path.GetFileName(new string(_pathBuf, 0, (int)size));
                CloseHandle(p);
            }
            _procCache[pid] = exe;
        }
        return exe.Length > 0 && _excluded.Contains(exe);
    }

    /// <summary>Monitor-local rects of excluded programs' windows on this monitor.</summary>
    public void ExcludedRegion(RECT mon, List<RECT> blocked)
    {
        blocked.Clear();
        foreach (var w in _z)
        {
            if (!w.Excluded) continue;
            var r = w.R;
            if (r.Right <= mon.Left || r.Left >= mon.Right || r.Bottom <= mon.Top || r.Top >= mon.Bottom) continue;
            blocked.Add(new RECT { Left = r.Left - mon.Left, Top = r.Top - mon.Top, Right = r.Right - mon.Left, Bottom = r.Bottom - mon.Top });
        }
    }

    /// <summary>"Shovel everything": all drifts are cleared.</summary>
    public void ClearAll()
    {
        foreach (var d in _drifts.Values) d.Clear();
    }

    public void OverlaysChanged() => _overlaysChanged = true;

    // ------------------------------------------------------------------ scanning ---
    public void Scan(IReadOnlyList<Overlay> overlays, float dt)
    {
        _sinceScan += dt;
        bool must = _eventsDirty || _overlaysChanged || _sinceScan >= 1.0 || _hooks.Count == 0;
        if (!must)
        {
            foreach (var d in _drifts.Values) d.Relax();
            return;
        }
        float scanDt = (float)_sinceScan;
        _sinceScan = 0;
        _eventsDirty = _overlaysChanged = false;
        dt = scanDt;

        _overlays = overlays;
        _classCacheAge += dt;
        if (_classCacheAge > 30) { _classCache.Clear(); _procCache.Clear(); _classCacheAge = 0; }

        _z.Clear();
        EnumWindows(_enumProc, IntPtr.Zero);

        foreach (var d in _drifts.Values) d.Seen = false;

        foreach (var w in _z)
        {
            if (!w.Surface) continue;
            if (!_drifts.TryGetValue(w.Hwnd, out var d))
                _drifts[w.Hwnd] = d = new Drift(w.Hwnd, w.Taskbar ? DriftKind.Taskbar : DriftKind.Window);
            d.Place(w.R.Left, w.R.Top, w.R.Width, ScaleAt(w.R.Left + w.R.Width / 2, w.R.Top), dt, Spill);
            d.Seen = true;
        }

        // the "ground": bottom edge of every monitor
        for (int i = 0; i < overlays.Count; i++)
        {
            var o = overlays[i];
            var key = GroundKey(o);
            if (!_drifts.TryGetValue(key, out var g))
                _drifts[key] = g = new Drift(key, DriftKind.Ground);
            g.Place(o.Bounds.Left, o.Bounds.Bottom, o.Bounds.Width, o.Scale, dt, Spill);
            g.Seen = true;
        }

        // windows that disappeared / got minimized: their snow falls down
        _gone.Clear();
        foreach (var (k, d) in _drifts) if (!d.Seen) _gone.Add(k);
        foreach (var k in _gone)
        {
            var d = _drifts[k];
            d.SpillAll(Spill);
            d.Dispose();
            _drifts.Remove(k);
        }

        foreach (var d in _drifts.Values) d.Relax();
    }

    private static IntPtr GroundKey(Overlay o) => new(-1000 - o.Bounds.Left * 7 - o.Bounds.Top * 13);

    private float ScaleAt(int x, int y)
    {
        foreach (var o in _overlays)
        {
            var b = o.Bounds;
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return o.Scale;
        }
        return _overlays.Count > 0 ? _overlays[0].Scale : 1f;
    }

    private bool OnWindow(IntPtr h, IntPtr _)
    {
        if (!IsWindowVisible(h) || IsIconic(h)) return true;

        uint ex = (uint)(long)GetWindowLongPtr(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TRANSPARENT) != 0) return true; // click-through overlays (ours and others')
        if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(h, out uint _, out byte alpha, out uint lf)
            && (lf & LWA_ALPHA) != 0 && alpha == 0) return true;

        string cls = ClassOf(h);
        if (cls.StartsWith("Sugrob.", StringComparison.Ordinal) || cls == "Progman" || cls == "WorkerW") return true;

        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, 16) != 0 && !GetWindowRect(h, out r)) return true;
        if (r.Width < 2 || r.Height < 2) return true;
        // cloaked = hidden by DWM (other virtual desktop, suspended UWP apps); checked last as it's the priciest call
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;

        bool taskbar = cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
        uint style = (uint)(long)GetWindowLongPtr(h, GWL_STYLE);
        bool framed = (style & (WS_CAPTION | WS_THICKFRAME)) != 0 || (ex & WS_EX_APPWINDOW) != 0;
        bool surface = taskbar ||
            (framed && (ex & WS_EX_TOOLWINDOW) == 0 && r.Width >= 120 && r.Height >= 60 && !CoversMonitor(r, style));

        bool excluded = _excluded.Count > 0 && !taskbar && IsExcluded(h);
        if (excluded) surface = false;
        _z.Add(new Win { Hwnd = h, R = r, Surface = surface, Taskbar = taskbar, Excluded = excluded });
        return true;
    }

    private bool CoversMonitor(RECT r, uint style)
    {
        if ((style & WS_CAPTION) == WS_CAPTION) return false;
        foreach (var o in _overlays)
        {
            var b = o.Bounds;
            if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) return true;
        }
        return false;
    }

    private string ClassOf(IntPtr h)
    {
        if (_classCache.TryGetValue(h, out var c)) return c;
        int n = GetClassName(h, _clsBuf, _clsBuf.Length);
        c = new string(_clsBuf, 0, Math.Max(0, n));
        _classCache[h] = c;
        return c;
    }

    // ------------------------------------------------------------------ segments ---
    /// <summary>Visible drift edges on one monitor, top of z-order first, ground last.</summary>
    public void BuildSegments(Overlay o, List<Seg> segs)
    {
        segs.Clear();
        var mon = o.Bounds;
        for (int zi = 0; zi < _z.Count; zi++)
        {
            var w = _z[zi];
            if (!w.Surface || !_drifts.TryGetValue(w.Hwnd, out var d)) continue;
            AddSegments(d, zi, mon, segs);
        }
        if (_drifts.TryGetValue(GroundKey(o), out var g)) AddSegments(g, _z.Count, mon, segs);
    }

    private void AddSegments(Drift d, int zLimit, RECT mon, List<Seg> segs)
    {
        int top = d.Top;
        if (top <= mon.Top || top > mon.Bottom) return;
        float a = Math.Max(d.Left, mon.Left), b = Math.Min(d.Left + d.Width, mon.Right);
        if (b - a < 1) return;

        _iv.Clear();
        _iv.Add((a, b));
        float bandTop = top - d.MaxH;
        for (int j = 0; j < zLimit && _iv.Count > 0; j++)
        {
            var r = _z[j].R;
            if (r.Bottom <= bandTop || r.Top >= top) continue; // doesn't touch the drift band
            Subtract(r.Left, r.Right);
        }

        foreach (var (x0, x1) in _iv)
        {
            if (x1 - x0 < 1) continue;
            segs.Add(new Seg
            {
                X0 = x0 - mon.Left,
                X1 = x1 - mon.Left,
                Top = top - mon.Top,
                OriginX = d.Left - mon.Left,
                Drift = d,
            });
        }
    }

    private void Subtract(float c, float e)
    {
        _ivTmp.Clear();
        foreach (var (a, b) in _iv)
        {
            if (e <= a || c >= b) { _ivTmp.Add((a, b)); continue; }
            if (c > a) _ivTmp.Add((a, c));
            if (e < b) _ivTmp.Add((e, b));
        }
        _iv.Clear();
        _iv.AddRange(_ivTmp);
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        foreach (var d in _drifts.Values) d.Dispose();
        _drifts.Clear();
    }
}
