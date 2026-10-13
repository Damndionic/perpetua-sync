namespace PerpetuaSync.Core;

/// <summary>
/// One game version inside the World of Warcraft folder (_classic_beta_, and whatever Forever's live folder is
/// called): where the addon's saved variables are, and the Perpetua addon folder that gets SiteData.lua.
/// </summary>
public sealed record Flavor(string Name, string Root)
{
    public string AddonDir => Path.Combine(Root, "Interface", "AddOns", "Perpetua");
    public bool HasAddon => Directory.Exists(AddonDir);

    public IEnumerable<string> SavedVariables()
    {
        var accounts = Path.Combine(Root, "WTF", "Account");
        if (!Directory.Exists(accounts)) yield break;
        foreach (var acct in Directory.EnumerateDirectories(accounts))
        {
            var f = Path.Combine(acct, "SavedVariables", "Perpetua.lua");
            if (File.Exists(f)) yield return f;
        }
    }
}

public static class WowFolder
{
    /// <summary>Where Battle.net installs WoW by default, if it's there.</summary>
    public static string? Guess()
    {
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(pf)) continue;
            var p = Path.Combine(pf, "World of Warcraft");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    static bool IsFlavorName(string name) => name.Length > 2 && name.StartsWith('_') && name.EndsWith('_');

    /// <summary>The game versions in a folder the person picked: the World of Warcraft folder (every _flavor_ in it)
    /// or one _flavor_ folder itself. Only ones with WTF or the Perpetua addon count.</summary>
    public static List<Flavor> Flavors(string? picked)
    {
        var list = new List<Flavor>();
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked)) return list;
        var full = Path.GetFullPath(picked);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IEnumerable<string> roots = IsFlavorName(name)
            ? [full]
            : Directory.EnumerateDirectories(full).Where(d => IsFlavorName(Path.GetFileName(d)));
        foreach (var root in roots.Order(StringComparer.OrdinalIgnoreCase))
        {
            var f = new Flavor(Path.GetFileName(root), root);
            if (f.HasAddon || Directory.Exists(Path.Combine(root, "WTF"))) list.Add(f);
        }
        return list;
    }

    /// <summary>A plain-English check of a picked folder, for the window.</summary>
    public static string Describe(List<Flavor> flavors)
    {
        if (flavors.Count == 0) return "No World of Warcraft game folders here. Pick the \"World of Warcraft\" folder.";
        var parts = flavors.Select(f =>
        {
            var saved = f.SavedVariables().Count();
            return $"{f.Name}: " + (f.HasAddon ? "addon installed" : "addon not installed") +
                   (saved > 0 ? $", {saved} account{(saved == 1 ? "" : "s")} with Perpetua data" : ", no Perpetua data yet");
        });
        return string.Join("; ", parts);
    }
}
