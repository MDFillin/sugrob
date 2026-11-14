using static Sugrob.Native;

namespace Sugrob;

/// <summary>Everything a global hotkey can do. The numeric value is the RegisterHotKey id.</summary>
internal enum HotkeyAction
{
    Pause = 1, WindLeft, WindRight, WindUp, WindDown, Calm, Intensity,
    Sculpt, Clear, Tool, Plow, Garland, Wallpaper, Quiet, Shape, GarlandShape,
    Settings, NewYear,
}

/// <summary>User-customisable global hotkeys: "Ctrl+Alt+Shift+S" style strings, stored in settings.</summary>
internal static class Hotkeys
{
    public static readonly (HotkeyAction Action, string Title, string Default)[] All =
    {
        (HotkeyAction.Pause, "Пауза / продолжить", "Ctrl+Alt+Shift+S"),
        (HotkeyAction.Intensity, "Сила снегопада (по кругу)", "Ctrl+Alt+Shift+W"),
        (HotkeyAction.Calm, "Штиль", "Ctrl+Alt+Shift+Space"),
        (HotkeyAction.WindLeft, "Ветер влево", "Ctrl+Alt+Shift+Left"),
        (HotkeyAction.WindRight, "Ветер вправо", "Ctrl+Alt+Shift+Right"),
        (HotkeyAction.WindUp, "Ветер вверх", "Ctrl+Alt+Shift+Up"),
        (HotkeyAction.WindDown, "Ветер вниз", "Ctrl+Alt+Shift+Down"),
        (HotkeyAction.Sculpt, "Лепить снежки и снеговиков", "Ctrl+Alt+Shift+B"),
        (HotkeyAction.Clear, "Убрать весь снег", "Ctrl+Alt+Shift+C"),
        (HotkeyAction.Tool, "Варежка / лопата", "Ctrl+Alt+Shift+T"),
        (HotkeyAction.Plow, "Вызвать снегоуборщик", "Ctrl+Alt+Shift+P"),
        (HotkeyAction.Garland, "Режим гирлянды", "Ctrl+Alt+Shift+G"),
        (HotkeyAction.GarlandShape, "Форма лампочек", "Ctrl+Alt+Shift+H"),
        (HotkeyAction.Wallpaper, "Живые обои", "Ctrl+Alt+Shift+L"),
        (HotkeyAction.Quiet, "Не мешать в развёрнутых окнах", "Ctrl+Alt+Shift+Q"),
        (HotkeyAction.Shape, "Форма снежинок", "Ctrl+Alt+Shift+F"),
        (HotkeyAction.Settings, "Открыть настройки", "Ctrl+Alt+Shift+O"),
        (HotkeyAction.NewYear, "Салют (новогодний режим)", "Ctrl+Alt+Shift+N"),
    };

    /// <summary>Arrow keys may auto-repeat (holding = stronger wind); the rest fire once per press.</summary>
    private static bool Repeats(HotkeyAction a) => a is HotkeyAction.WindLeft or HotkeyAction.WindRight or HotkeyAction.WindUp or HotkeyAction.WindDown;

    /// <summary>The combo in effect for an action ("" = turned off).</summary>
    public static string Get(Settings s, HotkeyAction a)
    {
        if (s.Hotkeys.TryGetValue(a.ToString(), out var v)) return v;
        foreach (var e in All) if (e.Action == a) return e.Default;
        return "";
    }

    /// <summary>Menu suffix like "\tCtrl+Alt+Shift+S" (empty if the hotkey is off).</summary>
    public static string Tab(Settings s, HotkeyAction a)
    {
        var k = Get(s, a);
        return k.Length == 0 ? "" : "\t" + k.Replace("Space", "Пробел");
    }

    /// <summary>Registers all hotkeys; returns the titles of those that clash with other programs.</summary>
    public static List<string> RegisterAll(IntPtr hwnd, Settings s)
    {
        var failed = new List<string>();
        foreach (var (a, title, _) in All)
        {
            UnregisterHotKey(hwnd, (int)a);
            var combo = Get(s, a);
            if (combo.Length == 0) continue;
            if (!TryParse(combo, out uint mods, out uint vk)) { failed.Add(title); continue; }
            if (!Repeats(a)) mods |= MOD_NOREPEAT;
            if (!RegisterHotKey(hwnd, (int)a, mods, vk)) failed.Add($"{title} ({combo})");
        }
        return failed;
    }

    public static void UnregisterAll(IntPtr hwnd)
    {
        foreach (var e in All) UnregisterHotKey(hwnd, (int)e.Action);
    }

    // ------------------------------------------------------------- text <-> keys ---
    private static readonly (string Name, uint Vk)[] Named =
    {
        ("Space", 0x20), ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08),
        ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Pause", 0x13), ("ScrollLock", 0x91), ("PrintScreen", 0x2C),
        ("Num0", 0x60), ("Num1", 0x61), ("Num2", 0x62), ("Num3", 0x63), ("Num4", 0x64),
        ("Num5", 0x65), ("Num6", 0x66), ("Num7", 0x67), ("Num8", 0x68), ("Num9", 0x69),
        ("NumMul", 0x6A), ("NumPlus", 0x6B), ("NumMinus", 0x6D), ("NumDot", 0x6E), ("NumDiv", 0x6F),
        (";", 0xBA), ("=", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF), ("`", 0xC0),
        ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE),
    };

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) return false;
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= MOD_CONTROL;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= MOD_ALT;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= MOD_SHIFT;
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= MOD_WIN;
            else
            {
                uint k = KeyFromName(p);
                if (k == 0) return false;
                vk = k;
            }
        }
        return vk != 0 && mods != 0; // a global hotkey without modifiers would steal the key everywhere
    }

    private static uint KeyFromName(string p)
    {
        if (p.Length == 1)
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if ((p[0] == 'F' || p[0] == 'f') && int.TryParse(p.AsSpan(1), out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        foreach (var (n, v) in Named) if (n.Equals(p, StringComparison.OrdinalIgnoreCase)) return v;
        return 0;
    }

    public static string Format(uint mods, uint vk)
    {
        var s = "";
        if ((mods & MOD_CONTROL) != 0) s += "Ctrl+";
        if ((mods & MOD_ALT) != 0) s += "Alt+";
        if ((mods & MOD_SHIFT) != 0) s += "Shift+";
        if ((mods & MOD_WIN) != 0) s += "Win+";
        return s + KeyName(vk);
    }

    public static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var (n, v) in Named) if (v == vk) return n;
        return "";
    }
}
