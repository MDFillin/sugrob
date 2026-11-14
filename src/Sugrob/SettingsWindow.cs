using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>
/// The settings window. Runs on its own STA thread with a WinForms message loop; every change
/// goes to the app through <see cref="App"/>'s Ui* methods (they run on the main thread).
/// The preview panel is drawn by the main render loop with the real snow engine.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private readonly App _app;
    private readonly SettingsSnapshot _snap;
    private Settings S => _snap.S;

    public static SettingsWindow Open(App app, SettingsSnapshot snap)
    {
        SettingsWindow? w = null;
        using var ready = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (!_styles) { Application.EnableVisualStyles(); _styles = true; }
            w = new SettingsWindow(app, snap);
            ready.Set();
            Application.Run(w);
        })
        { IsBackground = true, Name = "Sugrob settings" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        ready.Wait();
        return w!;
    }

    private static bool _styles;

    /// <summary>Shows the window on top again (callable from any thread).</summary>
    public void Raise()
    {
        try
        {
            BeginInvoke(() =>
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            });
        }
        catch (InvalidOperationException) { /* closing */ }
    }

    // ------------------------------------------------------------------ layout ---
    private const int TabW = 540, TabH = 520;
    private Font _hint = null!;

    private SettingsWindow(App app, SettingsSnapshot snap)
    {
        _app = app;
        _snap = snap;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        _hint = new Font("Segoe UI", 8.5f);
        Text = "Sugrob — настройки";
        ClientSize = new Size(TabW + 24 + 320, TabH + 24);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        try { if (Environment.ProcessPath is { } p) Icon = Icon.ExtractAssociatedIcon(p); } catch { /* no icon */ }

        var tabs = new TabControl { Location = new Point(12, 12), Size = new Size(TabW, TabH) };
        tabs.TabPages.Add(SnowTab());
        tabs.TabPages.Add(GarlandTab());
        tabs.TabPages.Add(PlowTab());
        tabs.TabPages.Add(WallpaperTab());
        tabs.TabPages.Add(ExclusionsTab());
        tabs.TabPages.Add(KeysTab());
        tabs.TabPages.Add(GeneralTab());
        Controls.Add(tabs);

        // preview + quick actions on the right
        int px = TabW + 24;
        var pvTitle = new Label { Text = "Предпросмотр", Location = new Point(px, 14), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) };
        Controls.Add(pvTitle);
        _preview = new PreviewPanel(_app) { Location = new Point(px, 38), Size = new Size(308, 330) };
        Controls.Add(_preview);
        Controls.Add(Hint("Так выглядят снег и гирлянды с текущими настройками. Всё применяется сразу.", px, 374, 308));

        int by = 420;
        Controls.Add(Button("Убрать весь снег", px, by, 150, () => _app.UiCommand(App.UiClear)));
        Controls.Add(Button("Вызвать уборщик", px + 158, by, 150, () => _app.UiCommand(App.UiPlowNow)));
        Controls.Add(Button("Показать салют", px, by + 34, 150, () => _app.UiFireworksTest()));
        var close = Button("Закрыть", px + 158, TabH - 18, 150, Close);
        Controls.Add(close);
        CancelButton = close;
        ResumeLayout(true);
    }

    private readonly PreviewPanel _preview;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // developer aid: SUGROB_SNAPSHOT=folder renders every tab to PNG (off-screen, no screen capture)
        if (Environment.GetEnvironmentVariable("SUGROB_SNAPSHOT") is { Length: > 0 } dir)
        {
            var tabs = Controls.OfType<TabControl>().First();
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                using var bmp = new Bitmap(Width, Height);
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(Path.Combine(dir, $"tab{i}.png"));
            }
            tabs.SelectedIndex = 0;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _app.UiPreviewGone();
        _app.UiClosed();
    }

    // ---------------------------------------------------------------- helpers ---
    private static TabPage Page(string title) => new(title) { Padding = new Padding(12), UseVisualStyleBackColor = true };

    private static Label Caption(string text, int x, int y) =>
        new() { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Segoe UI Semibold", 9f) };

    private Label Hint(string text, int x, int y, int w) =>
        new() { Text = text, Location = new Point(x, y), Size = new Size(w, 40), ForeColor = SystemColors.GrayText, Font = _hint };

    private static Button Button(string text, int x, int y, int w, Action click)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        b.Click += (_, _) => click();
        return b;
    }

    private static ComboBox Combo(int x, int y, int w, string[] items, int selected, Action<int> changed)
    {
        var c = new ComboBox { Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        c.SelectedIndexChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    private static CheckBox Check(string text, int x, int y, bool on, Action<bool> changed)
    {
        var c = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, Checked = on };
        c.CheckedChanged += (_, _) => changed(c.Checked);
        return c;
    }

    /// <summary>A column of radio buttons; values[i] is what option i stands for.</summary>
    private static void Radios(Control parent, int x, int y, string[] titles, int[] values, int current, Action<int> chosen, int step = 26)
    {
        for (int i = 0; i < titles.Length; i++)
        {
            int v = values[i];
            var r = new RadioButton { Text = titles[i], Location = new Point(x, y + i * step), AutoSize = true, Checked = v == current };
            r.CheckedChanged += (_, _) => { if (r.Checked) chosen(v); };
            parent.Controls.Add(r);
        }
    }

    // sizes in the order people expect (enum values are historical)
    private static readonly string[] SizeTitles = { "Крошечные", "Маленькие", "Средние", "Крупные", "Огромные" };
    private static readonly FlakeSize[] SizeOrder = { FlakeSize.Tiny, FlakeSize.Small, FlakeSize.Medium, FlakeSize.Large, FlakeSize.Huge };

    // ------------------------------------------------------------------- tabs ---
    private TabPage SnowTab()
    {
        var p = Page("Снег");
        int y = 14;
        p.Controls.Add(Caption("Сила снегопада", 14, y));
        Radios(p, 24, y + 24, new[] { "Лёгкий", "Ровный", "Метель" }, new[] { 0, 1, 2 }, (int)S.Intensity,
            v => _app.UiCommand(App.UiIntensity + v), 26);
        y += 112;

        p.Controls.Add(Caption("Снежинки", 14, y));
        p.Controls.Add(new Label { Text = "Форма", Location = new Point(24, y + 28), AutoSize = true });
        p.Controls.Add(Combo(130, y + 24, 200, new[] { "Смешанные", "Круглые", "Кристаллы", "Звёздочки", "Пушистые хлопья" },
            (int)S.FlakeShape, i => _app.UiCommand(App.UiShape + i)));
        p.Controls.Add(new Label { Text = "Размер", Location = new Point(24, y + 60), AutoSize = true });
        p.Controls.Add(Combo(130, y + 56, 200, SizeTitles, Array.IndexOf(SizeOrder, S.FlakeSize),
            i => _app.UiCommand(App.UiSize + (int)SizeOrder[i])));
        y += 100;

        p.Controls.Add(Caption("Ветер", 14, y));
        var windText = new Label { Location = new Point(380, y + 30), AutoSize = true };
        var wind = new TrackBar
        {
            Location = new Point(20, y + 22), Width = 350, Minimum = -25, Maximum = 25, TickFrequency = 5,
            Value = Math.Clamp((int)MathF.Round(S.WindX * 10), -25, 25), SmallChange = 1, LargeChange = 5,
            BackColor = SystemColors.Window,
        };
        void ShowWind() => windText.Text = wind.Value == 0 ? "штиль" : wind.Value < 0 ? $"← влево {-wind.Value / 10f:0.0}" : $"вправо {wind.Value / 10f:0.0} →";
        ShowWind();
        wind.ValueChanged += (_, _) => { ShowWind(); _app.UiWind(wind.Value / 10f); };
        p.Controls.Add(wind);
        p.Controls.Add(windText);
        y += 76;

        p.Controls.Add(Caption("Плавность", 14, y));
        int[] fps = { 0, 30, 60, 120, 144 };
        p.Controls.Add(Combo(130, y - 3, 250,
            new[] { $"Как у монитора ({_snap.Rates})", "30 кадров/с (экономно)", "60 кадров/с", "120 кадров/с", "144 кадра/с" },
            Math.Max(0, Array.IndexOf(fps, S.FpsLimit)), i => _app.UiFps(fps[i])));
        y += 36;

        p.Controls.Add(Caption("Чем смахивать снег", 14, y));
        p.Controls.Add(Combo(130, y - 3, 250, new[] { "Варежка — аккуратно смахнуть", "Лопата — убрать до конца" },
            (int)S.Tool, i => _app.UiTool((Tool)i)));
        y += 40;

        p.Controls.Add(Check("Не мешать в развёрнутых окнах (видео, браузер на весь экран)", 16, y, S.QuietOverMaximized, _app.UiQuiet));
        return p;
    }

    private TabPage GarlandTab()
    {
        var p = Page("Гирлянды");
        p.Controls.Add(Caption("Как светятся", 14, 14));
        // menu order (enum values are historical)
        Radios(p, 24, 38,
            new[] { "Выключены", "Горят", "Мигают", "Бегут волной", "Мерцают", "Переливаются радугой", "Бегущий огонёк" },
            new[] { 0, 3, 1, 2, 4, 5, 6 }, (int)S.Garland, v => _app.UiCommand(App.UiGarland + v));

        int y = 38 + 7 * 26 + 16;
        p.Controls.Add(Caption("Лампочки", 14, y));
        p.Controls.Add(new Label { Text = "Форма", Location = new Point(24, y + 28), AutoSize = true });
        p.Controls.Add(Combo(130, y + 24, 200, new[] { "Классические", "Шарики", "Звёздочки", "Сосульки", "Ретро-лампы" },
            (int)S.GarlandShape, i => _app.UiCommand(App.UiGarlandShape + i)));
        p.Controls.Add(new Label { Text = "Размер", Location = new Point(24, y + 60), AutoSize = true });
        p.Controls.Add(Combo(130, y + 56, 200, SizeTitles, Array.IndexOf(SizeOrder, S.GarlandSize),
            i => _app.UiCommand(App.UiGarlandSize + (int)SizeOrder[i])));
        p.Controls.Add(Hint("Гирлянды висят на верхних краях окон, над сугробами.", 16, y + 100, 480));
        return p;
    }

    private TabPage PlowTab()
    {
        var p = Page("Уборщик");
        p.Controls.Add(Caption("Кто чистит снег внизу экрана", 14, 14));
        var titles = new[] { "Не приезжает", "Трактор", "Ретро-трактор", "Снегоход", "Пингвин с лопатой", "Своя картинка" };
        var radios = new List<RadioButton>();
        for (int i = 0; i < titles.Length; i++)
        {
            int v = i;
            var r = new RadioButton { Text = titles[i], Location = new Point(24, 38 + i * 26), AutoSize = true, Checked = (int)S.Plow == i };
            if (v == (int)PlowKind.Custom) r.Enabled = _snap.HasImage;
            r.CheckedChanged += (_, _) => { if (r.Checked) _app.UiCommand(App.UiPlow + v); };
            radios.Add(r);
            p.Controls.Add(r);
        }
        int y = 38 + titles.Length * 26 + 12;
        var pick = Button("Выбрать свою картинку…", 24, y, 200, () => { });
        pick.Click += (_, _) => _app.UiPickImage(ok =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (!ok) return;
                    var custom = radios[(int)PlowKind.Custom];
                    custom.Enabled = true;
                    custom.Checked = true;
                });
            }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        });
        p.Controls.Add(pick);
        p.Controls.Add(Button("Вызвать сейчас", 232, y, 150, () => _app.UiCommand(App.UiPlowNow)));
        p.Controls.Add(Hint("Уборщик приезжает сам, когда внизу экрана наметает высокий сугроб. Своя картинка — PNG или JPG, лучше с прозрачным фоном.", 16, y + 44, 480));
        return p;
    }

    private TabPage WallpaperTab()
    {
        var p = Page("Обои");
        p.Controls.Add(Caption("Живые обои", 14, 14));
        Radios(p, 24, 38, new[] { "Выключены — ваши обои", "Домик в зимнем лесу", "Крыльцо" }, new[] { 0, 1, 2 },
            (int)S.Wallpaper, v => _app.UiCommand(App.UiWall + v));
        p.Controls.Add(Hint("Обои рисуются под значками рабочего стола. Когда окна закрывают экран целиком, обои засыпают и не тратят ресурсы.", 16, 130, 480));
        return p;
    }

    private TabPage ExclusionsTab()
    {
        var p = Page("Исключения");
        p.Controls.Add(Caption("Программы, над которыми снега нет", 14, 14));
        p.Controls.Add(Hint("Над окнами этих программ снег не идёт и не копится — например, Photoshop.exe или obs64.exe.", 16, 36, 490));

        var list = new ListBox { Location = new Point(16, 78), Size = new Size(300, 250), IntegralHeight = false };
        foreach (var a in S.ExcludedApps) list.Items.Add(a);
        p.Controls.Add(list);

        void Save()
        {
            var apps = list.Items.Cast<string>().ToList();
            S.ExcludedApps = apps;
            _app.UiExcluded(new List<string>(apps));
        }
        void AddName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim());
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            if (list.Items.Cast<string>().Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            list.Items.Add(name);
            Save();
        }

        var running = new ComboBox { Location = new Point(330, 78), Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        void Refresh()
        {
            running.Items.Clear();
            foreach (var n in RunningApps()) running.Items.Add(n);
            if (running.Items.Count > 0) running.SelectedIndex = 0;
        }
        Refresh();
        running.DropDown += (_, _) => { var keep = running.SelectedItem; Refresh(); if (keep != null) running.SelectedItem = keep; };
        p.Controls.Add(new Label { Text = "Открытые программы:", Location = new Point(330, 58), AutoSize = true });
        p.Controls.Add(running);
        p.Controls.Add(Button("Добавить", 330, 110, 180, () => AddName(running.SelectedItem as string)));
        p.Controls.Add(Button("Выбрать .exe файл…", 330, 150, 180, () =>
        {
            using var dlg = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Программа-исключение" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddName(dlg.FileName);
        }));
        p.Controls.Add(Button("Убрать из списка", 330, 190, 180, () =>
        {
            if (list.SelectedIndex < 0) return;
            list.Items.RemoveAt(list.SelectedIndex);
            Save();
        }));
        return p;
    }

    /// <summary>Exe names of programs that currently have a visible window (for the picker).</summary>
    private static List<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        foreach (var pr in Process.GetProcesses())
        {
            try
            {
                if (pr.Id == self || pr.MainWindowHandle == IntPtr.Zero) continue;
                names.Add(pr.ProcessName + ".exe");
            }
            catch { /* access denied / exited */ }
            finally { pr.Dispose(); }
        }
        names.Remove("explorer.exe");
        names.Remove("ApplicationFrameHost.exe");
        return names.ToList();
    }

    // -------------------------------------------------------------------- keys ---
    private readonly Dictionary<HotkeyAction, HotkeyBox> _keyBoxes = new();
    private Label _keyStatus = null!;

    private TabPage KeysTab()
    {
        var p = Page("Клавиши");
        p.Controls.Add(Hint("Щёлкните поле и нажмите новое сочетание (нужен Ctrl, Alt, Shift или Win). Backspace — отключить, Esc — отмена.", 16, 10, 500));
        var scroll = new Panel { Location = new Point(10, 52), Size = new Size(510, 370), AutoScroll = true };
        int y = 4;
        foreach (var (a, title, _) in Hotkeys.All)
        {
            scroll.Controls.Add(new Label { Text = title, Location = new Point(4, y + 4), Size = new Size(250, 20), AutoEllipsis = true });
            var box = new HotkeyBox { Location = new Point(258, y), Width = 200, Text = Display(Hotkeys.Get(S, a)) };
            var action = a;
            box.Entered += () => _app.UiSuspendHotkeys(true);
            box.Left_ += () => _app.UiSuspendHotkeys(false);
            box.Chosen += combo => SetKey(action, combo);
            _keyBoxes[a] = box;
            scroll.Controls.Add(box);
            y += 30;
        }
        p.Controls.Add(scroll);
        _keyStatus = new Label { Location = new Point(16, 430), Size = new Size(500, 40), Font = _hint };
        p.Controls.Add(_keyStatus);
        p.Controls.Add(Button("Вернуть стандартные", 16, 470 - 6, 180, () =>
        {
            S.Hotkeys.Clear();
            foreach (var (a, _, d) in Hotkeys.All) _keyBoxes[a].Text = Display(d);
            ApplyKeys();
        }));
        ShowFailures(_snap.HotkeyFailures);
        return p;
    }

    private static string Display(string combo) => combo.Length == 0 ? "— выключено —" : combo;

    private void SetKey(HotkeyAction a, string combo)
    {
        // one combo can't do two things: the older binding is switched off
        if (combo.Length > 0)
            foreach (var (other, _, _) in Hotkeys.All)
                if (other != a && Hotkeys.Get(S, other).Equals(combo, StringComparison.OrdinalIgnoreCase))
                {
                    S.Hotkeys[other.ToString()] = "";
                    _keyBoxes[other].Text = Display("");
                }
        S.Hotkeys[a.ToString()] = combo;
        _keyBoxes[a].Text = Display(combo);
        ApplyKeys();
    }

    private void ApplyKeys()
    {
        _app.UiHotkeys(new Dictionary<string, string>(S.Hotkeys), failed =>
        {
            try { BeginInvoke(() => ShowFailures(failed)); }
            catch (InvalidOperationException) { /* closed */ }
        });
    }

    private void ShowFailures(List<string> failed)
    {
        if (failed.Count == 0)
        {
            _keyStatus.ForeColor = Color.SeaGreen;
            _keyStatus.Text = "Все сочетания работают.";
        }
        else
        {
            _keyStatus.ForeColor = Color.Firebrick;
            _keyStatus.Text = "Заняты другой программой: " + string.Join(", ", failed) + ". Выберите другие сочетания.";
        }
    }

    // ------------------------------------------------------------------ general ---
    private TabPage GeneralTab()
    {
        var p = Page("Общие");
        p.Controls.Add(Check("Запускать вместе с Windows", 16, 16, _snap.Autostart, _app.UiAutostart));

        p.Controls.Add(Caption("Новый год", 14, 60));
        p.Controls.Add(Check("Обратный отсчёт и салют в полночь 31 декабря", 16, 84, S.NewYear, _app.UiNewYear));
        p.Controls.Add(Button("Показать салют сейчас", 16, 114, 200, () => _app.UiFireworksTest()));
        p.Controls.Add(Hint("Отсчёт идёт в последнюю минуту года, салют — пару минут после полуночи. Над видео во весь экран не показывается.", 16, 148, 490));

        p.Controls.Add(Caption("Обновления", 14, 206));
        var auto = Check("Обновляться автоматически", 16, 230, S.AutoUpdate, _app.UiAutoUpdate);
        auto.Enabled = _snap.CanUpdate;
        p.Controls.Add(auto);
        var check = Button("Проверить сейчас", 16, 260, 200, () => _app.UiCommand(App.UiUpdateCheck));
        check.Enabled = _snap.CanUpdate;
        p.Controls.Add(check);
        p.Controls.Add(Hint($"Установлена версия {_snap.Version}." + (_snap.CanUpdate ? "" : " Обновления работают в установленной копии."), 16, 296, 490));
        return p;
    }
}

/// <summary>Records a key combination when focused.</summary>
internal sealed class HotkeyBox : TextBox
{
    public event Action? Entered, Left_;
    public event Action<string>? Chosen;
    private string _before = "";

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        _before = Text;
        Text = "нажмите сочетание…";
        Entered?.Invoke();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        if (Text == "нажмите сочетание…" || Text.EndsWith('…')) Text = _before;
        Left_?.Invoke();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
        Keys key = keyData & Keys.KeyCode;
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mods |= MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mods |= MOD_SHIFT;
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) mods |= MOD_WIN;

        if (mods == 0 && key == Keys.Escape) { Text = _before; Parent?.Parent?.Focus(); return true; }
        if (mods == 0 && (key == Keys.Back || key == Keys.Delete)) { _before = ""; Chosen?.Invoke(""); Parent?.Parent?.Focus(); return true; }
        if (mods == 0 && key == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);

        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            Text = Hotkeys.Format(mods, 0) + "…";
            return true;
        }
        string name = Hotkeys.KeyName((uint)key);
        if (name.Length == 0) { Text = "эта клавиша не подходит"; return true; }
        if (mods == 0) { Text = "добавьте Ctrl, Alt, Shift или Win"; return true; }
        string combo = Hotkeys.Format(mods, (uint)key);
        _before = combo;
        Text = combo;
        Chosen?.Invoke(combo);
        Parent?.Parent?.Focus();
        return true;
    }

    // Alt+key arrives as WM_SYSKEYDOWN; make sure the form doesn't treat it as a menu mnemonic.
    protected override bool ProcessDialogKey(Keys keyData) => Focused || base.ProcessDialogKey(keyData);
}

/// <summary>
/// The preview area: paints a night sky and a pretend window; the app draws real snow,
/// drifts and garlands on top of it through DirectComposition.
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private readonly App _app;

    public PreviewPanel(App app)
    {
        _app = app;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
    }

    private Rectangle Mock
    {
        get
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            return new Rectangle(w * 12 / 100, h * 40 / 100, w * 76 / 100, h * 38 / 100);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Attach();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _app.UiPreviewGone();
        base.OnHandleDestroyed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) { Attach(); Invalidate(); }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (IsHandleCreated) Attach();
    }

    private void Attach()
    {
        var m = Mock;
        if (ClientSize.Width < 10 || ClientSize.Height < 10) return;
        _app.UiPreview(Handle, ClientSize.Width, ClientSize.Height, DeviceDpi / 96f,
            new RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Bottom });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;
        using (var sky = new LinearGradientBrush(r, Color.FromArgb(18, 30, 58), Color.FromArgb(52, 78, 120), LinearGradientMode.Vertical))
            g.FillRectangle(sky, r);

        // the pretend app window
        var m = Mock;
        float k = DeviceDpi / 96f;
        int title = (int)(26 * k);
        using (var body = new SolidBrush(Color.FromArgb(243, 245, 249))) g.FillRectangle(body, m);
        using (var bar = new SolidBrush(Color.FromArgb(222, 228, 238))) g.FillRectangle(bar, m.X, m.Y, m.Width, title);
        using (var dot = new SolidBrush(Color.FromArgb(150, 160, 175)))
            for (int i = 0; i < 3; i++)
                g.FillRectangle(dot, m.Right - (int)((14 + i * 18) * k), m.Y + title / 2 - (int)(1 * k), (int)(9 * k), (int)(2 * k));
        using (var line = new SolidBrush(Color.FromArgb(214, 220, 230)))
            for (int i = 0; i < 4; i++)
                g.FillRectangle(line, m.X + (int)(14 * k), m.Y + title + (int)((14 + i * 16) * k), (int)((m.Width - 28 * k) * (i == 3 ? 0.5f : 1f)), (int)(7 * k));
        using (var border = new Pen(Color.FromArgb(120, 0, 0, 0))) g.DrawRectangle(border, m);
    }
}
