using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sugrob;

/// <summary>
/// Updates from GitHub Releases of <see cref="Repo"/>.
/// 1. In the background: ask the GitHub API for the latest release; if its tag (vX.Y.Z) is newer,
///    download the "Sugrob.exe" asset next to the running exe as Sugrob.new.exe and check its
///    size and, if the release has "Sugrob.exe.sha256", its SHA-256.
/// 2. Apply (from the tray menu, or automatically on the next start): a running exe can be
///    renamed on Windows, so Sugrob.exe → Sugrob.old.exe, Sugrob.new.exe → Sugrob.exe, start it, exit.
///    The new copy deletes Sugrob.old.exe.
/// Only the release info and the exe are downloaded; nothing about the user is sent.
/// </summary>
internal static class Updater
{
    public const string Repo = "MDFillin/sugrob";

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static string Dir => Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
    private static string NewExe => Path.Combine(Dir, "Sugrob.new.exe");
    private static string NewVersionFile => Path.Combine(Dir, "Sugrob.new.version");
    private static string OldExe => Path.Combine(Dir, "Sugrob.old.exe");

    /// <summary>Developer builds (bin\...) don't update themselves.</summary>
    public static bool Enabled =>
        !(Environment.ProcessPath ?? "").Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !Repo.StartsWith("OWNER/");

    /// <summary>Version downloaded and ready to install, if any.</summary>
    public static Version? Ready
    {
        get
        {
            try
            {
                if (File.Exists(NewExe) && File.Exists(NewVersionFile) &&
                    Version.TryParse(File.ReadAllText(NewVersionFile).Trim(), out var v) && v > Current) return v;
            }
            catch { }
            return null;
        }
    }

    /// <summary>Called first thing on start: removes the previous exe after an update.</summary>
    public static void CleanupAfterUpdate()
    {
        for (int i = 0; i < 10 && File.Exists(OldExe); i++)
        {
            try { File.Delete(OldExe); } catch { Thread.Sleep(300); }
        }
        // a stale download that isn't newer than us
        try
        {
            if (File.Exists(NewExe) && Ready == null) { File.Delete(NewExe); File.Delete(NewVersionFile); }
        }
        catch { }
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sugrob-Updater", CurrentText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public enum Result { UpToDate, Downloaded, Failed }

    /// <summary>Checks GitHub and downloads a newer version (runs on a worker thread).</summary>
    public static async Task<(Result result, Version? version, string? notes)> CheckAndDownloadAsync()
    {
        try
        {
            using var http = Http();
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return (Result.Failed, null, null);
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            var cur = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            if (latest <= cur) return (Result.UpToDate, latest, null);
            if (Ready is { } r && r >= latest) return (Result.Downloaded, latest, null);

            string? exeUrl = null, shaUrl = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.Equals("Sugrob.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.GetProperty("size").GetInt64();
                }
                else if (name.Equals("Sugrob.exe.sha256", StringComparison.OrdinalIgnoreCase))
                    shaUrl = a.GetProperty("browser_download_url").GetString();
            }
            if (exeUrl == null) return (Result.Failed, latest, null);

            string tmp = NewExe + ".part";
            await using (var src = await http.GetStreamAsync(exeUrl))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            if (new FileInfo(tmp).Length != size) { File.Delete(tmp); return (Result.Failed, latest, null); }
            if (shaUrl != null)
            {
                string expected = (await http.GetStringAsync(shaUrl)).Trim().Split(' ', '\t', '\n')[0];
                await using var f = File.OpenRead(tmp);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); return (Result.Failed, latest, null); }
            }
            File.Move(tmp, NewExe, overwrite: true);
            File.WriteAllText(NewVersionFile, latest.ToString(3));
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            return (Result.Downloaded, latest, notes);
        }
        catch
        {
            return (Result.Failed, null, null);
        }
    }

    /// <summary>Swaps the exe and starts the new version. Returns false if it couldn't.</summary>
    public static bool Apply()
    {
        if (Ready == null) return false;
        string self = Environment.ProcessPath ?? "";
        try
        {
            if (File.Exists(OldExe)) File.Delete(OldExe);
            File.Move(self, OldExe);              // allowed while running
            File.Move(NewExe, self);
            try { File.Delete(NewVersionFile); } catch { }
            Process.Start(new ProcessStartInfo(self, "--updated") { UseShellExecute = false });
            return true;
        }
        catch
        {
            // roll back if the swap failed half-way
            try { if (!File.Exists(self) && File.Exists(OldExe)) File.Move(OldExe, self); } catch { }
            return false;
        }
    }
}
