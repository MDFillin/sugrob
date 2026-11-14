namespace Sugrob;

internal static class Program
{
    private const string MutexName = @"Local\Sugrob.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            Installer.Uninstall(quiet: args.Contains("--quiet"));
            return 0;
        }

        Updater.CleanupAfterUpdate();

        var mutex = new Mutex(false, MutexName);
        // after an update the previous copy may still be closing: wait for it a little
        bool first;
        try { first = mutex.WaitOne(args.Contains("--updated") ? 15000 : 0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            Native.MessageBox(IntPtr.Zero, "Sugrob уже запущен — ищите снежинку в трее у часов.", "Sugrob", 0x40);
            return 0;
        }

        try
        {
            // a downloaded copy offers to install itself (per user, no admin)
            if (Installer.OfferInstall())
            {
                mutex.ReleaseMutex();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
                return 0;
            }

            // an update downloaded last time is installed before anything starts
            if (Settings.Load().AutoUpdate && Updater.Enabled && Updater.Ready != null)
            {
                mutex.ReleaseMutex();
                if (Updater.Apply()) return 0;
                mutex.WaitOne(0);
            }

            using var app = new App();
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sugrob");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
            }
            catch { }
            Native.MessageBox(IntPtr.Zero, "Sugrob завершился с ошибкой:\n\n" + ex.Message, "Sugrob", 0x10);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }
}
