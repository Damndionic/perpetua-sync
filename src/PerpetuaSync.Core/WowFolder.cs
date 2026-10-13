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

    /// <summary>A game folder's name for people: "_classic_beta_" -> "Classic beta".</summary>
    public static string Friendly(string flavor)
    {
        var s = flavor.Trim('_').Replace('_', ' ');
        return s.Length == 0 ? flavor : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>One game's line in the picker: "Perpetua addon found, 1 account with guild data".</summary>
    public static string Detail(Flavor f)
    {
        if (!f.HasAddon) return "Perpetua addon not installed";
        var saved = f.SavedVariables().Count();
        return "Perpetua addon found" + (saved > 0 ? $", {saved} account{(saved == 1 ? "" : "s")} with guild data" : ", no guild data yet");
    }

    /// <summary>The game to suggest when nothing's chosen yet: one named Forever with the addon, else the only one with
    /// the addon. Null when it's not clear: then the person picks.</summary>
    public static Flavor? Suggest(List<Flavor> flavors)
    {
        var withAddon = flavors.Where(f => f.HasAddon).ToList();
        return withAddon.FirstOrDefault(f => f.Name.Contains("forever", StringComparison.OrdinalIgnoreCase))
               ?? (withAddon.Count == 1 ? withAddon[0] : null);
    }

    /// <summary>A plain-English check of a picked folder, for the window.</summary>
    public static string Describe(List<Flavor> flavors)
    {
        if (flavors.Count == 0) return "No WoW game found in this folder. Pick the \"World of Warcraft\" folder itself.";
        var withAddon = flavors.Where(f => f.HasAddon).ToList();
        if (withAddon.Count == 0) return "Found WoW, but the Perpetua addon isn't installed. Install it, then come back.";
        return string.Join("  ·  ", withAddon.Select(f =>
        {
            var saved = f.SavedVariables().Count();
            return $"{Friendly(f.Name)}: Perpetua addon found" +
                   (saved > 0 ? $", {saved} account{(saved == 1 ? "" : "s")} with guild data" : ", no guild data yet (log in once with the addon)");
        }));
    }
}
