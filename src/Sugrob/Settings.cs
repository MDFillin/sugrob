using System.Text.Json;
using Microsoft.Win32;

namespace Sugrob;

/// <summary>What the cursor does to snow: a mitten brushes the top off, a shovel clears to the ground.</summary>
public enum Tool { Mitten = 0, Shovel = 1 }

/// <summary>User settings, stored as JSON in %APPDATA%\Sugrob\settings.json.</summary>
internal sealed class Settings
{
    public Intensity Intensity { get; set; } = Intensity.Normal;
    public float WindX { get; set; }
    public float WindY { get; set; }
    public bool Paused { get; set; }
    public int FpsLimit { get; set; } = 60;
    public Tool Tool { get; set; } = Tool.Mitten;
    public GarlandMode Garland { get; set; } = GarlandMode.Wave;
    public GarlandShape GarlandShape { get; set; } = GarlandShape.Classic;
    public FlakeSize GarlandSize { get; set; } = FlakeSize.Medium;
    /// <summary>Check GitHub for new versions and install them on the next start.</summary>
    public bool AutoUpdate { get; set; } = true;
    public PlowKind Plow { get; set; } = PlowKind.Tractor;
    public string? CustomImage { get; set; }
    public WallpaperKind Wallpaper { get; set; } = WallpaperKind.Off;
    /// <summary>Don't show falling snow over maximized / fullscreen windows (videos, browser).</summary>
    public bool QuietOverMaximized { get; set; } = true;
    public FlakeShape FlakeShape { get; set; } = FlakeShape.Mixed;
    public FlakeSize FlakeSize { get; set; } = FlakeSize.Medium;
    /// <summary>Programs (exe names, e.g. "obs64.exe") over which snow is never shown.</summary>
    public List<string> ExcludedApps { get; set; } = new();
    /// <summary>Hotkey overrides: action name -> "Ctrl+Alt+Shift+S" ("" = off). Missing = default.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>New Year: countdown before midnight on 31 December and fireworks at midnight.</summary>
    public bool NewYear { get; set; } = true;

    public static string DataDir => Dir;

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sugrob");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { /* corrupted file -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not critical */ }
    }

    // ---- Autostart (per-user, no admin) -------------------------------------------
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Sugrob";

    public static bool Autostart
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{(Installer.IsInstalled ? Installer.InstalledExe : Environment.ProcessPath)}\"");
            else k.DeleteValue(RunName, false);
        }
    }
}
