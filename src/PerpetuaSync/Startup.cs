using Microsoft.Win32;

namespace PerpetuaSync;

/// <summary>
/// "Start with Windows": a value in this Windows user's Run key (HKCU, no administrator needed), launching the exe
/// with --tray so it goes straight to the tray. Each launch points it at wherever the exe is now, so moving the file
/// doesn't leave a dead entry behind once the app has been opened from its new place.
/// </summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string Name(bool dev) => dev ? "Perpetua Sync (dev)" : "Perpetua Sync";

    static string Command(bool dev) => $"\"{Environment.ProcessPath}\" --tray" + (dev ? " --dev" : "");

    public static void Apply(bool on, bool dev)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on) { if ((string?)key.GetValue(Name(dev)) != Command(dev)) key.SetValue(Name(dev), Command(dev)); }
            else key.DeleteValue(Name(dev), throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // A locked-down PC: the app still works, it just won't start by itself.
        }
    }
}
