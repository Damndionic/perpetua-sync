using PerpetuaSync.Core;

namespace PerpetuaSync;

static class Program
{
    // PerpetuaSync.exe           syncs with perpetua.gg
    // PerpetuaSync.exe --dev     syncs with dev.perpetua.gg (testing; its own settings and key)
    [STAThread]
    static int Main(string[] args)
    {
        var dev = args.Contains("--dev");
        // One copy per Windows user (two would upload the same things twice). Opening it again brings the running one
        // to the front (it may be hidden in the tray).
        var name = dev ? @"Local\PerpetuaSync-dev" : @"Local\PerpetuaSync";
        using var single = new Mutex(true, name, out var first);
        using var showMe = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
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
        Application.Run(new MainForm(dev ? SiteClient.Dev : SiteClient.Live, dev, showMe));
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
