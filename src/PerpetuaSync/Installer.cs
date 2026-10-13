using System.Diagnostics;

namespace PerpetuaSync;

/// <summary>
/// Where the app lives: %LOCALAPPDATA%\Programs\Perpetua Sync\PerpetuaSync.exe, for this Windows user only (no
/// administrator). The first run from anywhere else (Downloads, say) copies itself there, adds a Start menu shortcut,
/// and carries on from the copy, so Start with Windows and updates don't depend on where the download was saved.
/// Updates swap the exe in that folder: Windows lets a running exe be renamed, so the old one steps aside, the new
/// one takes its name, and the app restarts.
/// </summary>
static class Installer
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Perpetua Sync");
    public static string Exe => Path.Combine(Folder, "PerpetuaSync.exe");
    static string Old => Path.Combine(Folder, "PerpetuaSync.old.exe");
    public static string Pending => Path.Combine(Folder, "PerpetuaSync.update.exe");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Perpetua Sync.lnk");

    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    static Version? VersionOf(string exe)
    {
        try { return Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) ? v : null; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Running from somewhere else: put this exe in the app folder (unless a newer one is already there) and
    /// start that one. Returns false if it couldn't (then this copy just runs where it is).</summary>
    public static bool InstallAndRelaunch(IEnumerable<string> args)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var mine = VersionOf(Environment.ProcessPath!);
            var there = File.Exists(Exe) ? VersionOf(Exe) : null;
            if (there == null || (mine != null && mine >= there)) File.Copy(Environment.ProcessPath!, Exe, true);
            MakeShortcut();
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false }.WithArgs(args));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static ProcessStartInfo WithArgs(this ProcessStartInfo psi, IEnumerable<string> args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Start menu → Perpetua Sync. Best effort (Windows' own shortcut maker, through its scripting object).</summary>
    static void MakeShortcut()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            var link = shell.CreateShortcut(Shortcut);
            link.TargetPath = Exe;
            link.WorkingDirectory = Folder;
            link.Description = "Syncs the Perpetua addon with perpetua.gg";
            link.IconLocation = Exe + ",0";
            link.Save();
        }
        catch (Exception) { /* no shortcut is fine: the app works without one */ }
    }

    /// <summary>Left over from the last update: the exe that stepped aside. Gone once it isn't running.</summary>
    public static void CleanUp()
    {
        try { if (File.Exists(Old)) File.Delete(Old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Swaps in the downloaded update (already checked) and starts it. The caller exits right after.</summary>
    public static void ApplyAndRestart(IEnumerable<string> args)
    {
        if (File.Exists(Old)) File.Delete(Old);
        File.Move(Exe, Old);
        try { File.Move(Pending, Exe); }
        catch { File.Move(Old, Exe); throw; } // put the working one back
        Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false }.WithArgs(args.Append("--updated")));
    }
}
