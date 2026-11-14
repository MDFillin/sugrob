using System.Diagnostics;
using Microsoft.Win32;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>
/// Per-user install / uninstall without administrator rights:
/// the exe goes to %LOCALAPPDATA%\Programs\Sugrob, a Start-menu shortcut is created, autostart is
/// turned on, and an entry appears in Settings → Apps (HKCU) whose "Uninstall" runs <c>Sugrob.exe --uninstall</c>.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sugrob";
    private const string Version = "1.0.0";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sugrob");
    public static string InstalledExe => Path.Combine(InstallDir, "Sugrob.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sugrob.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>
    /// First start of a downloaded copy: offers to install. Returns true if the caller should exit
    /// (the installed copy has been started instead).
    /// </summary>
    public static bool OfferInstall()
    {
        if (IsRunningInstalled || Debugger.IsAttached) return false;
        string? self = Environment.ProcessPath;
        if (self == null || !self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        // developer builds (bin\...) never ask
        if (self.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false;

        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_TOP = 0x10000 | 0x40000; // SETFOREGROUND | TOPMOST
        int r = MessageBox(IntPtr.Zero,
            "Установить Sugrob на этот компьютер?\n\n" +
            "• программа будет в меню «Пуск» и запустится вместе с Windows;\n" +
            "• права администратора не нужны;\n" +
            "• удалить можно в «Параметры → Приложения».\n\n" +
            "«Нет» — просто запустить без установки.",
            "Sugrob", MB_YESNO | MB_ICONQUESTION | MB_TOP);
        if (r != 6 /* IDYES */) return false;

        try
        {
            Install(self);
            return true; // the caller releases the single-instance mutex and starts the installed copy
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, "Не получилось установить: " + ex.Message + "\n\nSugrob запустится без установки.", "Sugrob", 0x30 | 0x50000);
            return false;
        }
    }

    private static void Install(string sourceExe)
    {
        StopRunningCopy();
        Directory.CreateDirectory(InstallDir);
        File.Copy(sourceExe, InstalledExe, overwrite: true);

        CreateShortcut(Shortcut, InstalledExe);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Sugrob — снег на рабочем столе");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Sugrob");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("Sugrob", $"\"{InstalledExe}\"");
    }

    /// <summary>Runs from Settings → Apps → Uninstall.</summary>
    public static void Uninstall(bool quiet)
    {
        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_TOP = 0x10000 | 0x40000; // SETFOREGROUND | TOPMOST
        if (!quiet && MessageBox(IntPtr.Zero, "Удалить Sugrob с этого компьютера?", "Sugrob", MB_YESNO | MB_ICONQUESTION | MB_TOP) != 6)
            return;

        StopRunningCopy();
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("Sugrob", false);
        }
        catch { }
        try { if (File.Exists(Shortcut)) File.Delete(Shortcut); } catch { }
        try { Directory.Delete(Settings.DataDir, recursive: true); } catch { }

        // the running exe can't delete itself: a short-lived cmd removes the folder after we exit
        if (Directory.Exists(InstallDir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        if (!quiet) MessageBox(IntPtr.Zero, "Sugrob удалён. Ваши обои и настройки Windows не менялись.", "Sugrob", 0x40 | 0x50000);
    }

    /// <summary>Asks a running Sugrob to exit (it removes its snow and tray icon itself).</summary>
    private static void StopRunningCopy()
    {
        IntPtr h = FindWindow("Sugrob.Main", null);
        if (h == IntPtr.Zero) return;
        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40 && FindWindow("Sugrob.Main", null) != IntPtr.Zero; i++) Thread.Sleep(100);
        Thread.Sleep(300);
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.Description = "Снег на рабочем столе";
        sc.IconLocation = target + ",0";
        sc.Save();
    }
}
