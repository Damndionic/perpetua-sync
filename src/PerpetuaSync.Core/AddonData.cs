using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PerpetuaSync.Core;

/// <summary>A character profile found in the saved variables, ready to upload.</summary>
public sealed record Profile(string Name, JsonObject Data, string Guild, string File)
{
    public long Time => AddonData.Long(Data["t"]);
}

/// <summary>
/// What the Perpetua addon keeps in its saved variables (WTF/Account/&lt;acct&gt;/SavedVariables/Perpetua.lua), turned
/// into what the site's /api/sync/* endpoints take. A port of sync/perpetua_sync.py; keep the two in step.
/// </summary>
public static partial class AddonData
{
    const int MaxUnpackedBytes = 32 * 1024 * 1024;

    public static JsonObject ReadDb(string path)
    {
        var all = LuaReader.ReadFile(path);
        return all.TryGetValue("PerpetuaDB", out var db) && db is JsonObject o ? o : new JsonObject();
    }

    public static long Long(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (long)d : 0
        : 0;

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    [GeneratedRegex(@"^PERP1(Z?):(.+)$", RegexOptions.Singleline)]
    private static partial Regex Packed();

    /// <summary>The addon's "PERP1Z:" (base64 of deflate(JSON)) / "PERP1:" (base64 of JSON) strings.</summary>
    public static JsonNode? Unpack(JsonNode? node)
    {
        var s = Str(node);
        if (s == null) return null;
        var m = Packed().Match(s);
        if (!m.Success) return null;
        byte[] raw;
        try { raw = Convert.FromBase64String(m.Groups[2].Value); }
        catch (FormatException) { return null; }
        if (m.Groups[1].Value == "Z")
        {
            raw = Inflate(raw);
            if (raw.Length == 0) return null;
        }
        try { return JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }); }
        catch (JsonException) { return null; }
    }

    // zlib, raw deflate or gzip, whichever it is, capped so a hostile string can't fill the memory.
    static byte[] Inflate(byte[] data)
    {
        foreach (var make in new Func<Stream, Stream>[]
                 {
                     s => new ZLibStream(s, CompressionMode.Decompress),
                     s => new DeflateStream(s, CompressionMode.Decompress),
                     s => new GZipStream(s, CompressionMode.Decompress),
                 })
        {
            try
            {
                using var z = make(new MemoryStream(data));
                using var output = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    if (output.Length > MaxUnpackedBytes) return [];
                }
                if (output.Length > 0) return output.ToArray();
            }
            catch (InvalidDataException) { }
        }
        return [];
    }

    /// <summary>Profile + recipes as the site expects them (ns.merge in the addon). Since addon 2.13 a guildmate's full
    /// profile and recipes are kept packed (pz, rz) and "profile" is only a summary.</summary>
    public static JsonObject? Merge(JsonObject rec)
    {
        var profile = rec["profile"] as JsonObject;
        if (Truthy(rec["pz"]) && Unpack(rec["pz"]) is JsonObject full) profile = full;
        else if (profile != null && Truthy(profile["summary"])) return null; // a summary can't stand in for the profile
        if (profile == null) return null;

        var recipes = (rec["recipes"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        if (Truthy(rec["rz"]) && Unpack(rec["rz"]) is JsonObject packed && packed["r"] is JsonObject r)
            recipes = (JsonObject)r.DeepClone();

        var data = (JsonObject)profile.DeepClone();
        var profs = new JsonArray();
        var seen = new HashSet<string?>();
        if (profile["profs"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject p0) continue;
                var p = (JsonObject)p0.DeepClone();
                var name = Str(p["n"]);
                if (name != null && recipes[name] is JsonObject saved)
                {
                    p["recipes"] = Truthy(saved["list"]) ? saved["list"]!.DeepClone() : new JsonArray();
                    p["seen"] = saved["t"]?.DeepClone();
                }
                profs.Add(p);
                seen.Add(name);
            }
        }
        foreach (var (name, node) in recipes)
        {
            if (seen.Contains(name) || node is not JsonObject saved) continue;
            profs.Add(new JsonObject
            {
                ["n"] = name,
                ["recipes"] = Truthy(saved["list"]) ? saved["list"]!.DeepClone() : new JsonArray(),
                ["seen"] = saved["t"]?.DeepClone(),
                ["r"] = saved["r"]?.DeepClone(),
                ["m"] = saved["m"]?.DeepClone(),
                ["p"] = 1,
            });
        }
        data["profs"] = profs;
        var t = Math.Max(Long(rec["pt"]), Long(rec["rt"]));
        data["t"] = t > 0 ? t : null;
        return data;
    }

    static IEnumerable<(string Name, JsonObject Guild)> Guilds(JsonObject db) =>
        (db["guilds"] as JsonObject ?? []).Select(p => (p.Key, p.Value as JsonObject ?? new JsonObject()));

    /// <summary>Every character with a profile, by name; the newest copy wins. Files that can't be read are reported
    /// and skipped (WoW may be half-way through writing one).</summary>
    public static Dictionary<string, Profile> Collect(IEnumerable<(string Path, JsonObject Db)> dbs)
    {
        var found = new Dictionary<string, Profile>();
        foreach (var (path, db) in dbs)
            foreach (var (guild, g) in Guilds(db))
                foreach (var (name, recNode) in g["players"] as JsonObject ?? [])
                {
                    if (recNode is not JsonObject rec || Merge(rec) is not { } data) continue;
                    var p = new Profile(name, data, guild, path);
                    if (!found.TryGetValue(name, out var had) || p.Time > had.Time) found[name] = p;
                }
        return found;
    }

    /// <summary>Loot and rolls from every guild (duplicates are fine: the site drops them).</summary>
    public static (List<JsonObject> Loot, List<JsonObject> Rolls) CollectLoot(IEnumerable<(string Path, JsonObject Db)> dbs)
    {
        List<JsonObject> loot = [], rolls = [];
        foreach (var (_, db) in dbs)
            foreach (var (_, g) in Guilds(db))
            {
                loot.AddRange((g["loot"] as JsonArray ?? []).OfType<JsonObject>());
                rolls.AddRange((g["rolls"] as JsonArray ?? []).OfType<JsonObject>());
            }
        return (loot, rolls);
    }

    /// <summary>The newest guild roster the addon saved: (rosterAt, {"First Surname": {l, c, s}}).</summary>
    public static (long At, JsonObject Roster) CollectRoster(IEnumerable<(string Path, JsonObject Db)> dbs)
    {
        (long At, JsonObject Roster) best = (0, new JsonObject());
        foreach (var (_, db) in dbs)
            foreach (var (_, g) in Guilds(db))
            {
                var at = Long(g["rosterAt"]);
                if (g["roster"] is JsonObject roster && roster.Count > 0 && at > best.At) best = (at, roster);
            }
        return best;
    }

    /// <summary>The roster in the shape /api/sync/roster takes, sorted by name.</summary>
    public static JsonArray RosterMembers(JsonObject roster)
    {
        var members = new JsonArray();
        foreach (var (full, node) in roster.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (node is not JsonObject r) continue;
            var space = full.IndexOf(' ');
            var name = space < 0 ? full : full[..space];
            var surname = space < 0 ? "" : full[(space + 1)..];
            members.Add(new JsonObject
            {
                ["n"] = name,
                ["s"] = surname.Length > 0 ? surname : null,
                ["l"] = r["l"]?.DeepClone(),
                ["c"] = r["c"]?.DeepClone(),
                ["t"] = r["s"]?.DeepClone(),
            });
        }
        return members;
    }

    /// <summary>A short, stable hash of some data (keys sorted), to tell whether it changed since the last upload.</summary>
    public static string Fingerprint(JsonNode? data)
    {
        var sb = new StringBuilder();
        Canonical(sb, data);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    static void Canonical(StringBuilder sb, JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Canonical(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Canonical(sb, a[i]); }
                sb.Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(n.ToJsonString());
                break;
        }
    }
}
