using System.Collections.Concurrent;
using System.Numerics;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>
/// The bridge between the settings window (its own UI thread) and the app (main thread).
/// The window never touches app state directly: every change is posted here as a closure
/// and runs on the main thread between frames.
/// </summary>
internal sealed partial class App
{
    private const uint WM_UI = WM_APP + 7;
    private readonly ConcurrentQueue<Action> _ui = new();
    private SettingsWindow? _settingsWindow;

    /// <summary>Runs <paramref name="a"/> on the main thread (callable from any thread).</summary>
    private void Post(Action a)
    {
        _ui.Enqueue(a);
        PostMessage(_hwnd, WM_UI, IntPtr.Zero, IntPtr.Zero);
    }

    private void DrainUi()
    {
        while (_ui.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("UI action failed: " + ex); }
        }
    }

    /// <summary>A private copy of the live settings for the UI thread.</summary>
    private Settings SnapshotSettings()
    {
        _settings.Save();
        return Settings.Load();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { } w) { w.Raise(); return; }
        var snap = new SettingsSnapshot
        {
            S = SnapshotSettings(),
            Autostart = Settings.Autostart,
            Rates = string.Join(" и ", _overlays.Select(o => o.RefreshHz).Distinct().OrderByDescending(h => h).Select(h => h + " Гц")),
            HotkeyFailures = Hotkeys.RegisterAll(_hwnd, _settings),
            CanUpdate = Updater.Enabled,
            Version = Updater.CurrentText,
            HasImage = _scene.Image != null,
        };
        _settingsWindow = SettingsWindow.Open(this, snap);
    }

    /// <summary>Called by the window (UI thread) when it has closed.</summary>
    internal void UiClosed() => Post(() => { _settingsWindow = null; DetachPreview(); });

    // ------------------------------------------------------------ setting changes ---
    internal void UiCommand(int cmd) => Post(() => OnCommand(cmd));
    internal const int UiIntensity = CmdIntensity, UiShape = CmdShape, UiSize = CmdSize, UiGarland = CmdGarland,
        UiGarlandShape = CmdGarlandShape, UiGarlandSize = CmdGarlandSize, UiPlow = CmdPlow, UiWall = CmdWall,
        UiPlowNow = CmdPlowNow, UiPlowPick = CmdPlowPick, UiClear = CmdClear, UiUpdateCheck = CmdUpdateCheck;

    internal void UiWind(float x) => Post(() => SetWind(x, 0));
    internal void UiFps(int fps) => Post(() => { _settings.FpsLimit = fps; _settings.Save(); });
    internal void UiQuiet(bool on) => Post(() => { _settings.QuietOverMaximized = on; _settings.Save(); });
    internal void UiTool(Tool t) => Post(() => { _settings.Tool = t; _settings.Save(); UpdateTray(); });
    internal void UiAutoUpdate(bool on) => Post(() => { _settings.AutoUpdate = on; _settings.Save(); });
    internal void UiAutostart(bool on) => Post(() => { try { Settings.Autostart = on; } catch { /* blocked by policy */ } });
    internal void UiNewYear(bool on) => Post(() => { _settings.NewYear = on; _settings.Save(); });
    internal void UiPickImage(Action<bool> done) => Post(() =>
    {
        PickCustomImage();
        done(_settings.Plow == PlowKind.Custom && _scene.Image != null);
    });
    internal void UiFireworksTest() => Post(() => _newYear.StartTest(_clock.Elapsed.TotalSeconds));

    internal void UiExcluded(List<string> apps) => Post(() =>
    {
        _settings.ExcludedApps = apps;
        _settings.Save();
        _world.SetExcluded(apps);
    });

    /// <summary>Applies new hotkeys and reports (on the UI thread) the ones taken by other programs.</summary>
    internal void UiHotkeys(Dictionary<string, string> keys, Action<List<string>> done) => Post(() =>
    {
        _settings.Hotkeys = keys;
        _settings.Save();
        done(RegisterHotkeys(warn: false));
    });

    /// <summary>While a hotkey is being recorded our own hotkeys must not swallow the keys.</summary>
    internal void UiSuspendHotkeys(bool suspend) => Post(() =>
    {
        if (suspend) Hotkeys.UnregisterAll(_hwnd);
        else RegisterHotkeys(warn: false);
    });

    // -------------------------------------------------------------------- preview ---
    // The preview is a real Overlay drawn into a panel of the settings window by the
    // main render loop: same flakes, drifts and garlands as on the desktop.
    private IntPtr _pvHost;
    private int _pvW, _pvH;
    private float _pvScale = 1;
    private Overlay? _preview;
    private Drift? _pvWin, _pvGround;
    private readonly List<Spill> _pvSpill = new();
    private RECT _pvMock; // the pretend window inside the preview (panel pixels)

    internal void UiPreview(IntPtr host, int w, int h, float scale, RECT mock) => Post(() =>
    {
        DetachPreview();
        _pvHost = host; _pvW = w; _pvH = h; _pvScale = scale; _pvMock = mock;
    });

    internal void UiPreviewGone() => Post(DetachPreview);

    private void DetachPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _pvWin?.Dispose(); _pvWin = null;
        _pvGround?.Dispose(); _pvGround = null;
        _pvHost = IntPtr.Zero;
    }

    /// <summary>Called by RecreateGpu: the preview is rebuilt on the new device by the next frame.</summary>
    private void DropPreviewResources()
    {
        _preview?.Dispose();
        _preview = null;
        _pvWin?.Dispose(); _pvWin = null;
        _pvGround?.Dispose(); _pvGround = null;
    }

    // far away from every real monitor, so clumps/plows/snowballs never show up in the preview
    private const int PvX = -400000, PvY = -400000;

    private void RenderPreview(float dt, double now)
    {
        if (_pvHost == IntPtr.Zero || _gpu == null) return;
        if (!IsWindow(_pvHost)) { DetachPreview(); return; }
        if (_preview == null)
        {
            try
            {
                var b = new RECT { Left = PvX, Top = PvY, Right = PvX + _pvW, Bottom = PvY + _pvH };
                _preview = new Overlay(_gpu, b, _pvScale, _pvHost, _pvW, _pvH);
                _preview.Snow.Prefill(_settings.Intensity, _wind);
                _pvWin = new Drift(new IntPtr(-777001), DriftKind.Window);
                _pvGround = new Drift(new IntPtr(-777002), DriftKind.Ground);
            }
            catch { DetachPreview(); return; }
        }
        if (_preview.LastRender >= 0 && now - _preview.LastRender < 0.85 / 60) return;
        float pdt = _preview.LastRender < 0 ? dt : (float)Math.Min(now - _preview.LastRender, 0.1);
        _preview.LastRender = now;

        _pvSpill.Clear();
        _pvWin!.Place(PvX + _pvMock.Left, PvY + _pvMock.Top, _pvMock.Width, _pvScale, pdt, _pvSpill);
        _pvGround!.Place(PvX, PvY + _pvH, _pvW, _pvScale, pdt, _pvSpill);
        _pvSpill.Clear();
        _pvWin.Relax(); _pvGround.Relax();

        var segs = _preview.Segs;
        segs.Clear();
        segs.Add(new Seg { X0 = _pvMock.Left, X1 = _pvMock.Right, Top = _pvMock.Top, OriginX = _pvMock.Left, Drift = _pvWin });
        segs.Add(new Seg { X0 = 0, X1 = _pvW, Top = _pvH, OriginX = 0, Drift = _pvGround });

        bool paused = _settings.Paused;
        _preview.Snow.Update(paused ? 0 : pdt, _time, _settings.Intensity, _wind, segs, Overlay.FlakesAllowed);
        _preview.Render(_gpu, now, _scene);
    }
}

/// <summary>What the settings window needs to know when it opens.</summary>
internal sealed class SettingsSnapshot
{
    public Settings S = new();
    public bool Autostart;
    public string Rates = "";
    public List<string> HotkeyFailures = new();
    public bool CanUpdate;
    public string Version = "";
    public bool HasImage;
}
