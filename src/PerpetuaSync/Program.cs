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
        // One window at a time per Windows user (two would upload the same things twice).
        using var single = new Mutex(true, dev ? @"Local\PerpetuaSync-dev" : @"Local\PerpetuaSync", out var first);
        if (!first)
        {
            MessageBox.Show("Perpetua Sync is already open.", "Perpetua Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        // Anything unexpected is written to error.log and shown, and the window stays open.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception); e.SetObserved(); };
        ApplicationConfiguration.Initialize();
        Theme.Load();
        Application.Run(new MainForm(dev ? SiteClient.Dev : SiteClient.Live, dev));
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
