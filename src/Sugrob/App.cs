using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>Owns the hidden main window, tray icon, hotkeys, overlays and the frame loop.</summary>
internal sealed partial class App : IDisposable
{
    private const string MainClass = "Sugrob.Main";
    internal const string MainClassName = MainClass;
    internal const uint WM_OPEN_SETTINGS = Native.WM_APP + 8;

    // menu command ids
    private const int CmdPause = 1;
    private const int CmdIntensity = 10; // +Intensity
    private const int CmdWindCalm = 20, CmdWindLeft = 21, CmdWindLeftStrong = 22, CmdWindRight = 23, CmdWindRightStrong = 24;
    private const int CmdFps30 = 30, CmdFps60 = 31, CmdFps120 = 32, CmdFps144 = 33, CmdFpsMonitor = 34;
    private const int CmdAutostart = 40;
    private const int CmdSculpt = 50, CmdClear = 51, CmdToolMitten = 52, CmdToolShovel = 53;
    private const int CmdGarland = 60; // +GarlandMode
    private const int CmdPlow = 70;    // +PlowKind
    private const int CmdPlowPick = 80, CmdPlowNow = 81;
    private const int CmdWall = 90; // +WallpaperKind
    private const int CmdGarlandShape = 130; // +GarlandShape
    private const int CmdGarlandSize = 140;  // +FlakeSize
    private const int CmdQuiet = 100, CmdShape = 110 /* +FlakeShape */, CmdSize = 120 /* +FlakeSize */;
    private const int CmdExit = 99, CmdUninstall = 98, CmdUpdateCheck = 97, CmdUpdateAuto = 96, CmdUpdateNow = 95;
    private const int CmdSettings = 94;

    // hotkey ids
    private const int HkPause = (int)HotkeyAction.Pause, HkLeft = (int)HotkeyAction.WindLeft, HkRight = (int)HotkeyAction.WindRight, HkUp = (int)HotkeyAction.WindUp, HkDown = (int)HotkeyAction.WindDown, HkCalm = (int)HotkeyAction.Calm, HkIntensity = (int)HotkeyAction.Intensity;
    private const int HkSculpt = (int)HotkeyAction.Sculpt, HkClear = (int)HotkeyAction.Clear, HkTool = (int)HotkeyAction.Tool, HkPlow = (int)HotkeyAction.Plow, HkGarland = (int)HotkeyAction.Garland, HkWallpaper = (int)HotkeyAction.Wallpaper, HkQuiet = (int)HotkeyAction.Quiet, HkShape = (int)HotkeyAction.Shape, HkGarlandShape = (int)HotkeyAction.GarlandShape;
    private const int HkSettings = (int)HotkeyAction.Settings, HkNewYear = (int)HotkeyAction.NewYear;

    private readonly Settings _settings = Settings.Load();
    private readonly List<Overlay> _overlays = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WndProc? _proc;
    private IntPtr _hwnd;
    private uint _taskbarCreatedMsg;
    private TrayIcon? _tray;
    private Gpu? _gpu;
    private World _world = new();
    private readonly NewYear _newYear = new();
    private IntPtr _timer;

    private bool _rebuildPending;
    private double _rebuildAt;
    private Vector2 _wind;          // current (smoothed) wind
    private float _time;
    private double _lastTopmost;

    public void Run()
    {
        if (_profile)
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sugrob", "perf.log");
            Drift.Log = s => { try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { } };
        }
        Snowfall.Shape = _settings.FlakeShape;
        Snowfall.SizeSetting = _settings.FlakeSize;
        Garland.Shape = _settings.GarlandShape;
        Garland.SizeK = Snowfall.Factor(_settings.GarlandSize);
        CreateMainWindow();
        _tray = new TrayIcon(_hwnd);
        _tray.Show();
        UpdateTray();
        RegisterHotkeys();

        _timer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero) // pre-1803 Windows 10: regular timer
            _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);

        _wind = new Vector2(_settings.WindX, _settings.WindY);
        RebuildOverlays();
        Loop();
    }

    // ---------------------------------------------------------------- main loop ----
    private void Loop()
    {
        double last = _clock.Elapsed.TotalSeconds;
        double next = last;
        var handles = new[] { _timer };

        while (true)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            double now = _clock.Elapsed.TotalSeconds;
            if (_rebuildPending && now >= _rebuildAt) { _rebuildPending = false; RebuildOverlays(); }

            // nothing to do while paused or while the PC is locked
            bool active = !_settings.Paused && !_locked && _overlays.Count > 0;
            if (!active)
            {
                // Idle: sleep until a message arrives (or the rebuild timer is due).
                uint wait = _rebuildPending ? 250u : INFINITE;
                MsgWaitForMultipleObjectsEx(0, null, wait, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                last = next = _clock.Elapsed.TotalSeconds;
                continue;
            }

            // Windows 11: frames follow the compositor's own clock (vsync of the fastest monitor),
            // so motion is perfectly even; each monitor still draws only at its own rate (see Frame)
            if (!_idle && HasCompositorClock)
            {
                WaitForCompositorClock(40); // messages that arrive meanwhile are handled at the top of the loop
                now = _clock.Elapsed.TotalSeconds;
                if (AnyMonitorDue(now))
                {
                    float fdt = (float)Math.Min(now - last, 0.1);
                    last = now;
                    Frame(fdt, now);
                }
                next = now;
                continue;
            }

            if (now >= next)
            {
                float dt = (float)Math.Min(now - last, 0.1);
                last = now;
                Frame(dt, now);
                // nothing visible (fullscreen game/video on every monitor): a few checks per second are enough;
                // on battery / battery saver: at most 30 fps
                // the loop ticks at the fastest monitor's rate; slower monitors skip ticks (see Frame)
                int fps = 15;
                foreach (var o in _overlays) fps = Math.Max(fps, TargetFps(o));
                double interval = _idle ? 0.25 : 1.0 / fps;
                next += interval;
                if (next < now) next = now + interval; // we fell behind; don't try to catch up
            }

            double remaining = next - _clock.Elapsed.TotalSeconds;
            if (remaining > 0)
            {
                long due = -(long)(remaining * 10_000_000); // relative, 100ns units
                SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                MsgWaitForMultipleObjectsEx(1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            }
        }
    }

    private void Frame(float dt, double now)
    {
        _time += dt;
        if (now - _lastEnvCheck > 3) { _lastEnvCheck = now; CheckEnvironment(); }
        if (now >= _nextUpdateCheck) { _nextUpdateCheck = now + 6 * 3600; if (_settings.AutoUpdate) CheckForUpdates(manual: false); }
        var target = new Vector2(_settings.WindX, _settings.WindY) + Gust(_time);
        _wind += (target - _wind) * (1f - MathF.Exp(-dt * 1.5f));

        try
        {
            long t0 = Stopwatch.GetTimestamp();
            _world.Scan(_overlays, dt);
            DistributeSpill();
            foreach (var o in _overlays) _world.BuildSegments(o, o.Segs);
            CursorTools(dt, now);
            _balls.Update(dt, _world, _overlays, _virtualBottom);
            UpdatePlows(dt, now);
            DistributeSpill();
            _scene.Clumps.Update(dt, _overlays, _virtualBottom);
            _newYear.Update(dt, now, _settings.NewYear, _overlays);
            _scene.NewYear = _newYear;
            _scene.World = _world;
            _scene.Garland = _settings.Garland;
            _scene.Time = _time;
            long t1 = Stopwatch.GetTimestamp();
            long tSim = 0, tRender = 0;
            foreach (var o in _overlays)
            {
                // each monitor is drawn at its own rate (e.g. 240 Hz main + 60 Hz second screen)
                double need = 1.0 / TargetFps(o);
                if (o.LastRender >= 0 && now - o.LastRender < need * 0.85) continue;
                float odt = o.LastRender < 0 ? dt : (float)Math.Min(now - o.LastRender, 0.1);
                o.LastRender = now;
                o.RenderCount++;

                long a = Stopwatch.GetTimestamp();
                o.Quiet = _settings.QuietOverMaximized && _world.QuietRegion(o.Bounds, o.Allowed);
                _world.ExcludedRegion(o.Bounds, o.Blocked);
                o.Snow.Update(odt, _time, _settings.Intensity, _wind, o.Segs, o.ShowFlakes);
                long b = Stopwatch.GetTimestamp();
                o.Render(_gpu!, now, _scene);
                tRender += Stopwatch.GetTimestamp() - b;
                tSim += b - a;
            }
            if (_wall.Active && now - _lastWall >= 1.0 / 15) // live wallpaper: 15 fps is plenty for smoke and lights
            {
                _lastWall = now;
                if (_wall.Broken) _wall.Build(_gpu!, _overlays, _settings.Wallpaper); // Explorer restarted
                else _wall.Render(_gpu!, _time, _world);
            }
            RenderPreview(dt, now);
            _gpu!.Comp.Commit();
            if (_profile) Profile(t1 - t0, tSim, tRender, now);

            bool idle = _scene.Clumps.Count == 0 && _scene.Plows.Count == 0 && !_wall.Active && !_sculpt && _pvHost == IntPtr.Zero && !_newYear.Active;
            foreach (var o in _overlays) idle &= !o.ShowFlakes && o.Segs.Count == 0;
            _idle = idle;
        }
        catch (SharpGenException ex)
        {
            // Device lost (driver update, GPU reset, RDP switch...) — rebuild everything.
            Debug.WriteLine("GPU error: " + ex.Message);
            RecreateGpu();
            return;
        }

        if (now - _lastTopmost > 2.0)
        {
            _lastTopmost = now;
            foreach (var o in _overlays) o.KeepOnTop();
        }
    }

    // ---- cursor: mitten / shovel / sculpting ---------------------------------------
    private readonly WallpaperHost _wall = new();
    private double _lastWall;
    private readonly Scene _scene = new();
    private Snowballs _balls => _scene.Balls;
    private Vector2 _cursor, _cursorVel;
    private bool _cursorKnown, _lmbWas, _rmbWas, _escWas;
    private double _lastSweep = -10, _lastSculptAction;
    private bool _sculpt;
    private float _virtualBottom = 10000;

    private void CursorTools(float dt, double now)
    {
        GetCursorPos(out var pt);
        var c = new Vector2(pt.X, pt.Y);
        var prev = _cursorKnown ? _cursor : c;
        _cursorKnown = true;
        if (dt > 0) _cursorVel = Vector2.Lerp(_cursorVel, (c - prev) / dt, 0.5f);
        _cursor = c;

        if (_sculpt)
        {
            bool swapped = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
            bool lmb = (GetAsyncKeyState(swapped ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;
            bool rmb = (GetAsyncKeyState(swapped ? VK_LBUTTON : VK_RBUTTON) & 0x8000) != 0;
            bool esc = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
            bool pressed = lmb && !_lmbWas, released = !lmb && _lmbWas;
            if ((rmb && !_rmbWas) || (esc && !_escWas)) { SetSculpt(false); }
            else
            {
                if (lmb || released) _lastSculptAction = now;
                _balls.Input(c, _cursorVel, pressed, lmb, released, dt, _world, _overlays);
                if (lmb) _lastSweep = now; // keep drift shapes responsive while rolling
                if (now - _lastSculptAction > 120) SetSculpt(false); // forgotten mode switches itself off
            }
            _lmbWas = lmb; _rmbWas = rmb; _escWas = esc;
        }
        else Sweep(prev, c, now);
        FlushStrokes(now);

        // drift shapes rebuild quickly while the user plays with snow, lazily otherwise
        Drift.RebuildInterval = now - _lastSweep < 0.8 ? 1.0 / 30 : 0.25;
    }

    /// <summary>Snow the cursor has cut from one drift, gathered until it breaks off as a clump.</summary>
    private sealed class Stroke
    {
        public float Area, SumX, SumY, Scale;
        public double Last;
        public Vector2 Dir;
        public float Speed;
        public IntPtr Key;
    }

    private readonly Dictionary<IntPtr, Stroke> _strokes = new();
    private readonly List<IntPtr> _strokeKeys = new();

    /// <summary>
    /// Mitten / shovel. The cursor is a circle moving along a segment; snow it cuts from drifts is
    /// gathered while the stroke goes on and breaks off as big soft clumps that are pushed ahead of
    /// the cursor and fall smoothly. The mitten takes more the faster it moves; the shovel cuts whole columns.
    /// </summary>
    private void Sweep(Vector2 from, Vector2 to, double now)
    {
        bool shovel = _settings.Tool == Tool.Shovel;
        var path = to - from;
        float n = path.Length();
        if (!shovel && n < 0.5f) return;

        foreach (var o in _overlays)
        {
            var bo = o.Bounds;
            if (to.X < bo.Left - 60 || to.X > bo.Right + 60 || to.Y < bo.Top - 60 || to.Y > bo.Bottom + 60) continue;
            float s = o.Scale;
            float c = (shovel ? 36f : 24f) * s;                       // cursor radius
            float take = shovel ? 1f : MathF.Min(0.7f, n / (c * 1.2f)); // share of the overlap removed
            var a = from - new Vector2(bo.Left, bo.Top);
            var b = to - new Vector2(bo.Left, bo.Top);
            Vector2 Push(Vector2 p) => n > 0.5f ? path / n : new Vector2(p.X < b.X ? -1 : 1, -0.15f);
            float minX = MathF.Min(a.X, b.X) - c, maxX = MathF.Max(a.X, b.X) + c;
            bool any = false;

            foreach (var seg in o.Segs)
            {
                if (maxX < seg.X0 || minX > seg.X1) continue;
                var d = seg.Drift;
                int c0 = Math.Max(0, (int)((MathF.Max(minX, seg.X0) - seg.OriginX) / d.Cw));
                int c1 = Math.Min(d.H.Length - 1, (int)((MathF.Min(maxX, seg.X1) - seg.OriginX) / d.Cw));
                for (int m = c0; m <= c1; m++)
                {
                    float r = d.H[m];
                    if (r <= 0.02f) continue;
                    var col = new Vector2(seg.OriginX + (m + 0.5f) * d.Cw, seg.Top - r / 2);
                    if (col.X < seg.X0 || col.X >= seg.X1) continue;
                    var cp = Closest(col, a, b);
                    float g = col.X - cp.X;
                    if (MathF.Abs(g) >= c) continue;
                    float half = MathF.Sqrt(c * c - g * g);
                    float overlap = MathF.Max(0, MathF.Min(seg.Top, cp.Y + half) - MathF.Max(seg.Top - r, cp.Y - half));
                    float h = shovel && overlap > 0 ? r : MathF.Min(r, overlap * take);
                    if (h <= 0.02f) continue;
                    h = d.TakeColumn(m, h);
                    if (!_strokes.TryGetValue(d.Key, out var st)) _strokes[d.Key] = st = new Stroke { Key = d.Key };
                    float area = h * d.Cw;
                    st.Area += area;
                    // the clump forms just ahead of the cursor, at the height of the cut snow
                    var e = Push(col);
                    var at = new Vector2(bo.Left + b.X + e.X * c * 0.9f, bo.Top + MathF.Min(col.Y, b.Y));
                    st.SumX += at.X * area; st.SumY += at.Y * area;
                    st.Scale = s; st.Last = now; st.Dir = e; st.Speed = _cursorVel.Length();
                    any = true;
                }
            }
            if (any) _lastSweep = now;
        }
    }

    /// <summary>Releases the gathered snow as clumps when there's enough of it or the stroke paused.</summary>
    private void FlushStrokes(double now)
    {
        if (_strokes.Count == 0) return;
        _strokeKeys.Clear();
        _strokeKeys.AddRange(_strokes.Keys);
        foreach (var key in _strokeKeys)
        {
            var st = _strokes[key];
            if (st.Area <= 0) { if (now - st.Last > 2) _strokes.Remove(key); continue; }
            float s = st.Scale;
            bool due = st.Area >= 1300f * s * s || now - st.Last > 0.1;
            if (!due) continue;
            float speed = Math.Clamp(st.Speed * 0.3f, 90f * s, 380f * s);
            _scene.Clumps.Add(new Spill
            {
                X = st.SumX / st.Area, Y = st.SumY / st.Area, Area = st.Area,
                Vx = st.Dir.X * speed, Vy = st.Dir.Y * speed - 90f * s, Source = key,
            }, s);
            st.Area = st.SumX = st.SumY = 0;
        }
    }

    private static Vector2 Closest(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : 0;
        return a + ab * t;
    }

    private void SetSculpt(bool on)
    {
        if (on && _settings.Paused) return;
        _sculpt = on;
        _lmbWas = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        _rmbWas = true; _escWas = true; // ignore the keys that are held right now
        _lastSculptAction = _clock.Elapsed.TotalSeconds;
        if (!on) _balls.Input(_cursor, Vector2.Zero, false, false, true, 0, _world, _overlays); // drop what's held
        Overlay.SetInteractive(_overlays, on);
        UpdateTray();
    }

    private bool AnyMonitorDue(double now)
    {
        foreach (var o in _overlays)
            if (o.LastRender < 0 || now - o.LastRender >= 0.85 / TargetFps(o)) return true;
        return false;
    }

    /// <summary>
    /// Frames per second for a monitor: a fixed limit, or (FpsLimit = 0) the monitor's own refresh rate.
    /// On battery / battery saver it's capped at 30.
    /// </summary>
    private int TargetFps(Overlay o)
    {
        int fps = _settings.FpsLimit == 0 ? o.RefreshHz : _settings.FpsLimit;
        if (_lowPower) fps = Math.Min(fps, 30);
        return Math.Clamp(fps, 15, 360);
    }

    // ---- updates (GitHub Releases) -----------------------------------------------------
    private const uint WM_UPDATE_RESULT = WM_APP + 5;
    private double _nextUpdateCheck = 60; // first check a minute after start
    private bool _checking;
    private (Updater.Result result, Version? version) _updateResult;

    private void CheckForUpdates(bool manual)
    {
        if (_checking || !Updater.Enabled) return;
        _checking = true;
        Task.Run(async () =>
        {
            var (r, v, _) = await Updater.CheckAndDownloadAsync();
            _updateResult = (r, v);
            PostMessage(_hwnd, WM_UPDATE_RESULT, manual ? 1 : 0, IntPtr.Zero);
        });
    }

    private void OnUpdateResult(bool manual)
    {
        _checking = false;
        var (r, v) = _updateResult;
        string ver = v?.ToString(3) ?? "";
        switch (r)
        {
            case Updater.Result.Downloaded:
                _tray?.ShowBalloon($"Sugrob {ver} готов к установке",
                    "Установится при следующем запуске. Или сейчас: меню снежинки → «Обновить до " + ver + "».");
                break;
            case Updater.Result.UpToDate when manual:
                _tray?.ShowBalloon("Sugrob", $"У вас последняя версия ({Updater.CurrentText}).");
                break;
            case Updater.Result.Failed when manual:
                _tray?.ShowBalloon("Sugrob", "Не удалось проверить обновления. Проверьте интернет и попробуйте позже.");
                break;
        }
    }

    // ---- environment: power, accessibility, lock ---------------------------------------
    private bool _locked, _lowPower, _noAnim, _idle;
    private double _lastEnvCheck = -10;

    /// <summary>
    /// Battery / battery saver → lighter snow and ≤30 fps. Windows "Show animations" off → no falling
    /// snow (drifts stay), as the original app does. Checked every few seconds.
    /// </summary>
    private void CheckEnvironment()
    {
        bool low = GetSystemPowerStatus(out var ps) && (ps.ACLineStatus == 0 || (ps.SystemStatusFlag & 1) != 0);
        bool noAnim = SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int anim, 0) && anim == 0;
        if (low != _lowPower || noAnim != _noAnim)
        {
            _lowPower = low;
            _noAnim = noAnim;
            Snowfall.DensityK = low ? 0.55f : 1f;
            Overlay.FlakesAllowed = !noAnim;
            UpdateTray();
        }
    }

    // ---- snowplow --------------------------------------------------------------------
    private double _nextPlowCheck;
    private readonly Dictionary<int, double> _plowCooldown = new(); // per monitor (by left edge)
    private float _plowDir = 1;

    private void UpdatePlows(float dt, double now)
    {
        for (int i = _scene.Plows.Count - 1; i >= 0; i--)
        {
            var p = _scene.Plows[i];
            p.Update(dt, _world, _world.Spill);
            if (p.Done) _scene.Plows.RemoveAt(i);
        }
        if (_settings.Plow == PlowKind.Off || now < _nextPlowCheck) return;
        _nextPlowCheck = now + 1.0;
        foreach (var o in _overlays)
        {
            if (_plowCooldown.TryGetValue(o.Bounds.Left, out var until) && now < until) continue;
            var d = BottomDrift(o, out float fill);
            if (d != null && fill > 0.55f) StartPlow(o, d, now);
        }
    }

    /// <summary>The lowest wide drift on a monitor (taskbar edge or screen bottom) and how full it is (0..1).</summary>
    private static Drift? BottomDrift(Overlay o, out float fill)
    {
        Drift? best = null;
        float bestW = 0;
        fill = 0;
        foreach (var seg in o.Segs)
        {
            var d = seg.Drift;
            if (d.Kind == DriftKind.Window) continue;
            float w = seg.X1 - seg.X0;
            if (w > bestW) { bestW = w; best = d; }
        }
        if (best == null || bestW < 200 * o.Scale) return null;
        double sum = 0;
        int n = 0;
        foreach (var seg in o.Segs)
        {
            if (seg.Drift != best) continue;
            for (float x = seg.X0; x < seg.X1; x += best.Cw * 2) { sum += best.HeightAt(x - seg.OriginX); n++; }
        }
        fill = n > 0 ? (float)(sum / n / best.MaxH) : 0;
        return best;
    }

    private void StartPlow(Overlay o, Drift d, double now)
    {
        if (_scene.Plows.Any(p => p.Monitor.Left == o.Bounds.Left)) return;
        var kind = _settings.Plow == PlowKind.Off ? PlowKind.Tractor : _settings.Plow;
        if (kind == PlowKind.Custom && _scene.Image == null) kind = PlowKind.Tractor;
        _scene.Plows.Add(new Snowplow(kind, d, o.Bounds, o.Scale, _plowDir));
        _plowDir = -_plowDir; // next time from the other side
        _plowCooldown[o.Bounds.Left] = now + 40;
    }

    private void CallPlowNow()
    {
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var o in _overlays)
        {
            var d = BottomDrift(o, out _);
            if (d != null) StartPlow(o, d, now);
        }
    }

    private void PickCustomImage()
    {
        var path = ImageLoader.PickFile(_hwnd);
        if (path == null || _gpu == null) return;
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            string copy = Path.Combine(Settings.DataDir, "plow" + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, copy, true);
            var img = ImageLoader.Load(_gpu, copy);
            if (img == null)
            {
                MessageBox(_hwnd, "Не получилось открыть эту картинку. Попробуйте PNG или JPG.", "Sugrob", 0x30);
                return;
            }
            _scene.Image?.Dispose();
            _scene.Image = img;
            _settings.CustomImage = copy;
            _settings.Plow = PlowKind.Custom;
            _settings.Save();
            CallPlowNow();
        }
        catch (Exception ex)
        {
            MessageBox(_hwnd, "Не получилось сохранить картинку: " + ex.Message, "Sugrob", 0x30);
        }
    }

    /// <summary>New look for flakes right away (instead of waiting for old ones to fall).</summary>
    private void RefillSnow()
    {
        foreach (var o in _overlays) o.Snow.Prefill(_settings.Intensity, _wind);
    }

    private void SetWallpaper(WallpaperKind kind)
    {
        _settings.Wallpaper = kind;
        _settings.Save();
        if (_gpu == null) return;
        _wall.Build(_gpu, _overlays, kind);
        if (kind != WallpaperKind.Off && !_wall.Active)
            MessageBox(_hwnd, "Не получилось встроиться за значки рабочего стола: " + _wall.LastError, "Sugrob", 0x30);
    }

    private void ClearAllSnow()
    {
        _world.ClearAll();
        _balls.CrumbleAll(_world.Spill);
        _lastSweep = _clock.Elapsed.TotalSeconds;
    }

    // ---- optional profiling (set SUGROB_PROFILE=1) --------------------------------
    private readonly bool _profile = Environment.GetEnvironmentVariable("SUGROB_PROFILE") == "1";
    private long _pScan, _pSim, _pRender;
    private int _pFrames;
    private double _pStart;
    private long _pAllocStart;
    private int _pGcStart;

    private void Profile(long scan, long sim, long render, double now)
    {
        _pScan += scan; _pSim += sim; _pRender += render; _pFrames++;
        if (now - _pStart < 5) return;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - _pAllocStart;
        int gcs = GC.CollectionCount(0) - _pGcStart;
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency / Math.Max(1, _pFrames);
        var line = $"[{DateTime.Now:T}] frames={_pFrames} alloc/frame={allocated / Math.Max(1, _pFrames)}B gc0={gcs} scan={ms(_pScan):F3}ms sim={ms(_pSim):F3}ms render={ms(_pRender):F3}ms " +
                   $"(drifts={ms(Overlay.PDrifts):F3} sprites={ms(Overlay.PSprites):F3} endDraw={ms(Overlay.PEnd):F3} present={ms(Overlay.PPresent):F3}) n={_world.Drifts.Count()} preview={(_preview != null ? _preview.Segs.Count + "segs" : "off")} newyear={_newYear.Active}\n";
        Overlay.PDrifts = Overlay.PSprites = Overlay.PEnd = Overlay.PPresent = 0;
        foreach (var o in _overlays)
            line += $"   mon {o.Bounds.Left},{o.Bounds.Top} {o.RefreshHz}Hz fps={o.RenderCount / Math.Max(0.001, now - _pStart):F0} quiet={o.Quiet} allowed=[{string.Join("; ", o.Allowed.Select(r => $"{r.Left},{r.Top},{r.Right},{r.Bottom}"))}] blocked={o.Blocked.Count} clumps={_scene.Clumps.Count}\n";
        try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sugrob", "perf.log"), line); } catch { }
        _pScan = _pSim = _pRender = 0; _pFrames = 0; _pStart = now;
        foreach (var o in _overlays) o.RenderCount = 0;
        _pAllocStart = GC.GetAllocatedBytesForCurrentThread();
        _pGcStart = GC.CollectionCount(0);
    }

    /// <summary>Snow that left drifts (window closed, shaken, plow, ...) falls as soft clumps.</summary>
    private void DistributeSpill()
    {
        var spill = _world.Spill;
        if (spill.Count == 0) return;
        foreach (var d in spill)
        {
            float scale = _overlays.Count > 0 ? _overlays[0].Scale : 1f;
            foreach (var o in _overlays)
            {
                var b = o.Bounds;
                if (d.X >= b.Left && d.X < b.Right) { scale = o.Scale; break; }
            }
            _scene.Clumps.Add(d, scale);
        }
        spill.Clear();
    }

    private Vector2 Gust(float t)
    {
        float k = _settings.Intensity switch { Intensity.Blizzard => 1.3f, Intensity.Normal => 0.3f, _ => 0.15f };
        float gx = MathF.Sin(t * 0.31f) * 0.6f + MathF.Sin(t * 0.73f + 1.3f) * 0.3f + MathF.Sin(t * 1.9f + 0.4f) * 0.1f;
        float gy = MathF.Sin(t * 0.47f + 2.1f) * 0.25f;
        return new Vector2(gx, gy) * k;
    }

    // ---------------------------------------------------------------- overlays -----
    private void RebuildOverlays()
    {
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        _gpu ??= new Gpu();
        if (_scene.Image == null && _settings.CustomImage is { } img && File.Exists(img))
            _scene.Image = ImageLoader.Load(_gpu, img);

        var monitors = new List<(RECT rect, float scale, int hz)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            GetMonitorInfo(hMon, ref info);
            float scale = GetDpiForMonitor(hMon, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
            monitors.Add((info.rcMonitor, scale, RefreshRateOf(info.szDevice)));
            return true;
        }, IntPtr.Zero);

        foreach (var (rect, scale, hz) in monitors)
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var o = new Overlay(_gpu, rect, scale) { RefreshHz = hz };
            o.Snow.Prefill(_settings.Intensity, _wind);
            if (_settings.Paused) o.SetVisible(false);
            _overlays.Add(o);
        }
        _virtualBottom = _overlays.Count > 0 ? _overlays.Max(o => o.Bounds.Bottom) : 10000;
        _world.OverlaysChanged();
        _world.SetExcluded(_settings.ExcludedApps);
        if (_sculpt) Overlay.SetInteractive(_overlays, true);
        if (_settings.Wallpaper != WallpaperKind.Off) _wall.Build(_gpu, _overlays, _settings.Wallpaper);
    }

    private void RecreateGpu()
    {
        _wall.Dispose();
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        DropPreviewResources();
        _world.Dispose(); // drift geometries belong to the old D2D factory
        _world = new World();
        _scene.Plows.Clear();
        _scene.Image?.Dispose(); // bitmaps belong to the old device
        _scene.Image = null;
        _gpu?.Dispose();
        _gpu = null;
        ScheduleRebuild(0.5);
    }

    private void ScheduleRebuild(double delay = 0.6)
    {
        _rebuildPending = true;
        _rebuildAt = _clock.Elapsed.TotalSeconds + delay;
    }

    // ---------------------------------------------------------------- actions ------
    private void SetPaused(bool paused)
    {
        if (paused && _sculpt) SetSculpt(false);
        _settings.Paused = paused;
        foreach (var o in _overlays)
        {
            if (!paused) o.Snow.Prefill(_settings.Intensity, _wind);
            o.SetVisible(!paused);
        }
        _settings.Save();
        UpdateTray();
    }

    private void SetIntensity(Intensity i)
    {
        _settings.Intensity = i;
        _settings.Save();
        UpdateTray();
    }

    private void SetWind(float x, float y)
    {
        _settings.WindX = Math.Clamp(x, -3f, 3f);
        _settings.WindY = Math.Clamp(y, -2f, 2f);
        _settings.Save();
    }

    private void UpdateTray()
    {
        string state = _settings.Paused ? "на паузе" : _settings.Intensity switch
        {
            Intensity.Light => "лёгкий снег",
            Intensity.Blizzard => "метель",
            _ => "снег идёт",
        };
        string tool = _settings.Tool == Tool.Shovel ? "лопата" : "варежка";
        string tip = _sculpt
            ? "Sugrob — лепка: тяните снег по сугробу, кладите шар на шар. Esc или ПКМ — выход"
            : $"Sugrob — {state}, {tool}"
              + (_noAnim ? "\nВ Windows выключены анимации — падающий снег не показывается" : "")
              + (_lowPower ? "\nЭкономия батареи: снег легче" : "");
        if (tip.Length > 127) tip = tip[..127];
        _tray?.Update(_settings.Paused, tip);
    }

    // ---------------------------------------------------------------- window -------
    private void CreateMainWindow()
    {
        _proc = MainProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = MainClass,
        };
        RegisterClassEx(ref wc);
        // A hidden top-level (not message-only) window, so it receives broadcasts
        // such as WM_DISPLAYCHANGE and "TaskbarCreated".
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, MainClass, "Sugrob", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        WTSRegisterSessionNotification(_hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
    }

    /// <summary>(Re)registers the user's hotkeys; warns once about combos taken by other programs.</summary>
    public List<string> RegisterHotkeys(bool warn = true)
    {
        var failed = Hotkeys.RegisterAll(_hwnd, _settings);
        if (warn && failed.Count > 0)
            _tray?.ShowBalloon("Sugrob: заняты сочетания клавиш",
                "Их уже использует другая программа:\n" + string.Join("\n", failed.Take(4)) + "\nПоменять можно в настройках → «Клавиши».");
        return failed;
    }

    private IntPtr MainProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case TrayIcon.CallbackMessage:
                int ev = LOWORD(lParam);
                if (ev == (int)WM_LBUTTONUP) SetPaused(!_settings.Paused);
                else if (ev == (int)WM_RBUTTONUP || ev == (int)WM_CONTEXTMENU) ShowMenu();
                return IntPtr.Zero;

            case WM_OPEN_SETTINGS:
                if (wParam == 1) _newYear.StartTest(_clock.Elapsed.TotalSeconds); // "Sugrob.exe --fireworks"
                else OpenSettings();
                return IntPtr.Zero;

            case WM_UI:
                DrainUi();
                return IntPtr.Zero;

            case WM_HOTKEY:
                OnHotkey((int)wParam);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                ScheduleRebuild();
                break;

            case WM_SETTINGCHANGE:
                // work area / DPI / taskbar changes
                ScheduleRebuild();
                break;

            case WM_UPDATE_RESULT:
                OnUpdateResult(wParam != IntPtr.Zero);
                return IntPtr.Zero;

            case WM_WTSSESSION_CHANGE:
                // locked: the lock screen covers everything — stop working until unlock
                if ((int)wParam == WTS_SESSION_LOCK) _locked = true;
                else if ((int)wParam == WTS_SESSION_UNLOCK) { _locked = false; ScheduleRebuild(0.3); }
                return IntPtr.Zero;

            case WM_DESTROY:
                WTSUnRegisterSessionNotification(hWnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMsg && msg != 0) { _tray?.Recreate(); UpdateTray(); }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnHotkey(int id)
    {
        const float step = 0.5f;
        switch (id)
        {
            case HkPause: SetPaused(!_settings.Paused); break;
            case HkSettings: OpenSettings(); break;
            case HkNewYear: _newYear.StartTest(_clock.Elapsed.TotalSeconds); break;
            case HkIntensity: SetIntensity((Intensity)(((int)_settings.Intensity + 1) % 3)); break;
            case HkCalm: SetWind(0, 0); break;
            case HkLeft: SetWind(_settings.WindX - step, _settings.WindY); break;
            case HkRight: SetWind(_settings.WindX + step, _settings.WindY); break;
            case HkUp: SetWind(_settings.WindX, _settings.WindY - step); break;
            case HkDown: SetWind(_settings.WindX, _settings.WindY + step); break;
            case HkSculpt: SetSculpt(!_sculpt); break;
            case HkClear: if (!_settings.Paused) ClearAllSnow(); break;
            case HkPlow: if (!_settings.Paused) CallPlowNow(); break;
            case HkWallpaper: SetWallpaper((WallpaperKind)(((int)_settings.Wallpaper + 1) % 3)); break;
            case HkQuiet: _settings.QuietOverMaximized = !_settings.QuietOverMaximized; _settings.Save(); break;
            case HkShape:
                _settings.FlakeShape = Snowfall.Shape = (FlakeShape)(((int)_settings.FlakeShape + 1) % 5);
                _settings.Save(); RefillSnow(); break;
            case HkGarland:
                _settings.Garland = (GarlandMode)(((int)_settings.Garland + 1) % 7);
                _settings.Save();
                break;
            case HkGarlandShape:
                _settings.GarlandShape = Garland.Shape = (GarlandShape)(((int)_settings.GarlandShape + 1) % 5);
                _settings.Save();
                break;
            case HkTool:
                _settings.Tool = _settings.Tool == Tool.Mitten ? Tool.Shovel : Tool.Mitten;
                _settings.Save();
                UpdateTray();
                break;
        }
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        IntPtr snowMenu = CreatePopupMenu();
        try
        {
            uint Check(bool c) => c ? MF_CHECKED : 0;

            AppendMenu(snowMenu, Check(_settings.Intensity == Intensity.Light), CmdIntensity + 0, "Лёгкий");
            AppendMenu(snowMenu, Check(_settings.Intensity == Intensity.Normal), CmdIntensity + 1, "Ровный");
            AppendMenu(snowMenu, Check(_settings.Intensity == Intensity.Blizzard), CmdIntensity + 2, "Метель");

            AppendMenu(menu, 0, CmdSettings, "Настройки…" + Hotkeys.Tab(_settings, HotkeyAction.Settings));
            SetMenuDefaultItem(menu, CmdSettings, 0);
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, Check(_settings.Paused), CmdPause, "Пауза" + Hotkeys.Tab(_settings, HotkeyAction.Pause));
            AppendMenu(menu, MF_POPUP, snowMenu, "Снегопад");
            AppendMenu(menu, Check(_sculpt) | (_settings.Paused ? MF_GRAYED : 0), CmdSculpt, "Лепить снежки и снеговиков" + Hotkeys.Tab(_settings, HotkeyAction.Sculpt));
            AppendMenu(menu, _settings.Paused ? MF_GRAYED : 0, CmdClear, "Убрать весь снег" + Hotkeys.Tab(_settings, HotkeyAction.Clear));
            AppendMenu(menu, _settings.Paused ? MF_GRAYED : 0, CmdPlowNow, "Вызвать снегоуборщик" + Hotkeys.Tab(_settings, HotkeyAction.Plow));
            AppendMenu(menu, Check(_settings.QuietOverMaximized), CmdQuiet, "Не мешать в развёрнутых окнах" + Hotkeys.Tab(_settings, HotkeyAction.Quiet));
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, Check(Settings.Autostart), CmdAutostart, "Запускать вместе с Windows");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            if (Updater.Enabled)
            {
                if (Updater.Ready is { } ready)
                    AppendMenu(menu, 0, CmdUpdateNow, $"Обновить до {ready.ToString(3)}");
                AppendMenu(menu, _checking ? MF_GRAYED : 0, CmdUpdateCheck, $"Проверить обновления (сейчас {Updater.CurrentText})");
                AppendMenu(menu, Check(_settings.AutoUpdate), CmdUpdateAuto, "Обновляться автоматически");
                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }
            if (Installer.IsRunningInstalled) AppendMenu(menu, 0, CmdUninstall, "Удалить Sugrob…");
            AppendMenu(menu, 0, CmdExit, "Выход");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            OnCommand(cmd);
        }
        finally
        {
            DestroyMenu(menu); // destroys sub-menus too
        }
    }

    /// <summary>Five size steps, smallest to largest (enum values are not in order — kept for old settings).</summary>
    private static void AppendSizes(IntPtr menu, FlakeSize current, int baseCmd, string prefix)
    {
        (FlakeSize v, string name)[] items =
        {
            (FlakeSize.Tiny, "очень мелкие"), (FlakeSize.Small, "мелкие"), (FlakeSize.Medium, "средние"),
            (FlakeSize.Large, "крупные"), (FlakeSize.Huge, "очень крупные"),
        };
        foreach (var (v, name) in items)
            AppendMenu(menu, current == v ? MF_CHECKED : 0, baseCmd + (int)v, prefix + name);
    }

    private void OnCommand(int cmd)
    {
        switch (cmd)
        {
            case CmdPause: SetPaused(!_settings.Paused); break;
            case >= CmdIntensity and <= CmdIntensity + 2: SetIntensity((Intensity)(cmd - CmdIntensity)); break;
            case CmdWindCalm: SetWind(0, 0); break;
            case CmdWindLeft: SetWind(-1, 0); break;
            case CmdWindLeftStrong: SetWind(-2.5f, 0); break;
            case CmdWindRight: SetWind(1, 0); break;
            case CmdWindRightStrong: SetWind(2.5f, 0); break;
            case CmdFps30: _settings.FpsLimit = 30; _settings.Save(); break;
            case CmdFps60: _settings.FpsLimit = 60; _settings.Save(); break;
            case CmdFps120: _settings.FpsLimit = 120; _settings.Save(); break;
            case CmdFps144: _settings.FpsLimit = 144; _settings.Save(); break;
            case CmdFpsMonitor: _settings.FpsLimit = 0; _settings.Save(); break;
            case CmdAutostart:
                try { Settings.Autostart = !Settings.Autostart; } catch { /* registry blocked by policy */ }
                break;
            case CmdSculpt: SetSculpt(!_sculpt); break;
            case CmdClear: ClearAllSnow(); break;
            case CmdToolMitten: _settings.Tool = Tool.Mitten; _settings.Save(); break;
            case CmdToolShovel: _settings.Tool = Tool.Shovel; _settings.Save(); break;
            case >= CmdGarland and <= CmdGarland + 6:
                _settings.Garland = (GarlandMode)(cmd - CmdGarland); _settings.Save(); break;
            case >= CmdGarlandShape and <= CmdGarlandShape + 4:
                _settings.GarlandShape = Garland.Shape = (GarlandShape)(cmd - CmdGarlandShape); _settings.Save(); break;
            case >= CmdPlow and <= CmdPlow + 5:
                _settings.Plow = (PlowKind)(cmd - CmdPlow); _settings.Save();
                if (_settings.Plow != PlowKind.Off) CallPlowNow(); // show the choice right away
                break;
            case >= CmdWall and <= CmdWall + 2: SetWallpaper((WallpaperKind)(cmd - CmdWall)); break;
            case CmdQuiet: _settings.QuietOverMaximized = !_settings.QuietOverMaximized; _settings.Save(); break;
            case >= CmdShape and <= CmdShape + 4:
                _settings.FlakeShape = Snowfall.Shape = (FlakeShape)(cmd - CmdShape);
                _settings.Save(); RefillSnow(); break;
            case >= CmdSize and <= CmdSize + 4:
                _settings.FlakeSize = Snowfall.SizeSetting = (FlakeSize)(cmd - CmdSize);
                _settings.Save(); RefillSnow(); break;
            case >= CmdGarlandSize and <= CmdGarlandSize + 4:
                _settings.GarlandSize = (FlakeSize)(cmd - CmdGarlandSize);
                Garland.SizeK = Snowfall.Factor(_settings.GarlandSize);
                _settings.Save(); break;
            case CmdPlowPick: PickCustomImage(); break;
            case CmdPlowNow: CallPlowNow(); break;
            case CmdSettings: OpenSettings(); break;
            case CmdExit: DestroyWindow(_hwnd); break;
            case CmdUpdateCheck: CheckForUpdates(manual: true); break;
            case CmdUpdateAuto: _settings.AutoUpdate = !_settings.AutoUpdate; _settings.Save(); break;
            case CmdUpdateNow:
                if (Updater.Apply()) DestroyWindow(_hwnd); // the new version starts and takes over
                else _tray?.ShowBalloon("Sugrob", "Не получилось установить обновление.");
                break;
            case CmdUninstall:
                // the uninstaller asks for confirmation and closes this running copy itself
                Process.Start(new ProcessStartInfo(Installer.InstalledExe, "--uninstall") { UseShellExecute = false });
                break;
        }
    }

    public void Dispose()
    {
        _wall.Dispose(); // repaints the user's own wallpaper
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        DetachPreview();
        _newYear.Dispose();
        _world.Dispose();
        _gpu?.Dispose();
        _tray?.Dispose();
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }
}
