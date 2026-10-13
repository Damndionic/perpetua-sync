using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PerpetuaSync;

/// <summary>
/// What the app remembers, in %APPDATA%\Perpetua Sync\settings.json: the WoW folder, and the site key encrypted
/// with Windows' DPAPI for the signed-in Windows user. Another Windows user, or the file copied to another PC,
/// can't decrypt it.
/// </summary>
sealed class Settings
{
    public string? WowFolder { get; set; }
    public string? ProtectedKey { get; set; }
    /// <summary>What the window's X does: true = hide to the tray and keep syncing, false = exit, null = ask (first time).</summary>
    public bool? CloseToTray { get; set; }

    /// <summary>Start (in the tray) when this Windows user signs in. On unless turned off in Settings.</summary>
    public bool StartWithWindows { get; set; } = true;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Perpetua Sync");
    public static string StateFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perpetua Sync");

    static string FileFor(bool dev) => Path.Combine(Folder, dev ? "settings-dev.json" : "settings.json");

    public static Settings Load(bool dev)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileFor(dev))) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(bool dev)
    {
        Directory.CreateDirectory(Folder);
        var file = FileFor(dev);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, true);
    }

    public string? Key
    {
        get
        {
            if (ProtectedKey == null) return null;
            try { return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(ProtectedKey))); }
            catch (Exception e) when (e is FormatException or Win32Exception) { return null; } // another user's or PC's: connect again
        }
        set => ProtectedKey = value == null ? null : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>Windows' CryptProtectData for the current user, called directly (no extra package).</summary>
static class Dpapi
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Perpetua Sync site key v1");
    const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static byte[] Protect(byte[] data) => Run(data, true);
    public static byte[] Unprotect(byte[] data) => Run(data, false);

    static byte[] Run(byte[] data, bool protect)
    {
        var inH = GCHandle.Alloc(data, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = inH.AddrOfPinnedObject() };
            var entropy = new Blob { Size = Entropy.Length, Data = enH.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "Perpetua Sync", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { inH.Free(); enH.Free(); }
    }
}
