using PerpetuaSync.Core;

namespace PerpetuaSync;

static class Program
{
    // PerpetuaSync.exe           syncs with perpetua.gg
    // PerpetuaSync.exe --dev     syncs with dev.perpetua.gg (testing; its own settings and key)
    // PerpetuaSync.exe --tray    starts in the tray, no window (how Start with Windows launches it)
    // PerpetuaSync.exe --portable  runs where it is instead of installing itself (testing)
    // (--updated: started by an update; waits for the old copy to exit)
    [STAThread]
    static int Main(string[] args)
    {
        var dev = args.Contains("--dev");
        var name = dev ? @"Local\PerpetuaSync-dev" : @"Local\PerpetuaSync";
        using var showMe = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");

        // Run from the app folder (Installer.cs). A copy started from anywhere else installs itself and hands over; if
        // the installed one is already running, it just brings that one forward.
        if (!Installer.RunningInstalled && !args.Contains("--portable"))
        {
            if (Mutex.TryOpenExisting(name, out var running)) { running.Dispose(); showMe.Set(); return 0; }
            if (Installer.InstallAndRelaunch(args.Where(a => a != "--updated"))) return 0;
        }
        Installer.CleanUp();

        // One copy per Windows user (two would upload the same things twice). Opening it again brings the running one
        // to the front (it may be hidden in the tray). Right after an update, wait for the old copy to finish exiting.
        using var single = new Mutex(false, name);
        bool first;
        try { first = single.WaitOne(args.Contains("--updated") ? 20_000 : 0); }
        catch (AbandonedMutexException) { first = true; } // the previous copy exited without letting go (an update)
        if (!first)
        {
            showMe.Set();
            return 0;
        }
        // Anything unexpected is written to error.log and shown, and the window stays open.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception); e.SetObserved(); };
        ApplicationConfiguration.Initialize();
        Theme.Load();
        Application.Run(new MainForm(dev ? SiteClient.Dev : SiteClient.Live, dev, showMe, startHidden: args.Contains("--tray"), updated: args.Contains("--updated")));
        return 0;
    }

    public static string ErrorLog => Path.Combine(Settings.StateFolder, "error.log");

    public static void Report(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Settings.StateFolder);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}\r\n\r\n");
        }
        catch (IOException) { }
        MessageBox.Show($"Something went wrong:\n\n{e?.Message}\n\nDetails are in {ErrorLog}", "Perpetua Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
