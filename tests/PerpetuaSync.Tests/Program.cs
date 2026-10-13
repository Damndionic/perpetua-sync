// Tests for PerpetuaSync.Core, as a console program (no test framework, so no NuGet packages).
//   dotnet run --project tests/PerpetuaSync.Tests                         run the checks; ends "ALL PASS"
//   dotnet run --project tests/PerpetuaSync.Tests -- --dump <Perpetua.lua>   what would be uploaded, as canonical JSON
//                                                                          (tests/parity.sh compares it with the Python app)
//   dotnet run --project tests/PerpetuaSync.Tests -- --sitedata <in.json> <out.lua>   write a SiteData.lua from JSON
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using PerpetuaSync.Core;

if (args.Length >= 2 && args[0] == "--dump")
{
    var dbs = new List<(string, JsonObject)> { (args[1], AddonData.ReadDb(args[1])) };
    var profiles = new JsonObject();
    foreach (var (name, p) in AddonData.Collect(dbs).OrderBy(p => p.Key, StringComparer.Ordinal)) profiles[name] = p.Data.DeepClone();
    var (loot, rolls) = AddonData.CollectLoot(dbs);
    var (at, roster) = AddonData.CollectRoster(dbs);
    var dump = new JsonObject
    {
        ["profiles"] = profiles,
        ["loot"] = new JsonArray(loot.Select(x => (JsonNode)x.DeepClone()).ToArray()),
        ["rolls"] = new JsonArray(rolls.Select(x => (JsonNode)x.DeepClone()).ToArray()),
        ["roster_at"] = at,
        ["roster"] = AddonData.RosterMembers(roster),
    };
    Console.WriteLine(dump.ToJsonString());
    return 0;
}
if (args.Length >= 3 && args[0] == "--sitedata")
{
    var data = JsonNode.Parse(File.ReadAllText(args[1]));
    File.WriteAllText(args[2], "PerpetuaSiteData = " + LuaWriter.Value(data) + "\n");
    return 0;
}

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) failures++;
}
void Throws<T>(Action a, string what) where T : Exception
{
    try { a(); Check(false, what + " (didn't throw)"); }
    catch (T) { Check(true, what); }
    catch (Exception e) { Check(false, $"{what} (threw {e.GetType().Name}: {e.Message})"); }
}
Dictionary<string, JsonNode?> Lua(string s) => LuaReader.ReadAssignments(Encoding.UTF8.GetBytes(s));
string J(JsonNode? n) => n?.ToJsonString() ?? "null";

Console.WriteLine("Lua reader");
{
    var r = Lua("""
        -- a comment
        PerpetuaDB = {
          ["guilds"] = {
            ["Perpetua"] = { ["players"] = { ["Dion"] = { ["pt"] = 1700000000, ["x"] = -1.5, ["e"] = 1e3 } } },
          },
          ["list"] = { "a", "b", nil, },
          ["sparse"] = { [1] = "a", [3] = "c" },
          plain = true, ["no"] = false,
          ["esc"] = "q\"b\\n\n\065\195\169\
        x",
          [5] = 'single',
        }
        Other = nil
        """);
    var db = r["PerpetuaDB"] as JsonObject;
    Check(db != null, "reads the PerpetuaDB table");
    Check(J(db!["guilds"]!["Perpetua"]!["players"]!["Dion"]!["pt"]) == "1700000000", "integers stay integers");
    Check(J(db["guilds"]!["Perpetua"]!["players"]!["Dion"]!["x"]) == "-1.5", "negative decimals");
    Check(J(db["guilds"]!["Perpetua"]!["players"]!["Dion"]!["e"]) == "1000", "exponents");
    Check(db["list"] is JsonArray { Count: 3 }, "1..n tables are lists (a trailing nil kept as null, as Python does)");
    Check(db["sparse"] is JsonObject so && so.ContainsKey("1") && so.ContainsKey("3"), "gapped tables are objects with string keys");
    Check(J(db["plain"]) == "true" && J(db["no"]) == "false", "bare-word keys and booleans");
    Check((string?)db["esc"] == "q\"b\\n\nAé\nx", "escapes: \\\" \\\\ \\n \\ddd, UTF-8 bytes, escaped newline");
    Check((string?)db["5"] == "single", "single-quoted strings and number keys");
    Check(r.ContainsKey("Other") && r["Other"] == null, "nil assignments");
}
Throws<FormatException>(() => Lua("PerpetuaDB = os.execute(\"calc\")"), "refuses a function call (it's data, never code)");
Throws<FormatException>(() => Lua("PerpetuaDB = { [\"a\"] = 1 + 2 }"), "refuses expressions");
Throws<FormatException>(() => Lua("PerpetuaDB = { \"unterminated }"), "refuses an unterminated string");
Throws<FormatException>(() => Lua("PerpetuaDB = { { { "), "refuses an unterminated table");
Throws<FormatException>(() => Lua("PerpetuaDB = " + new string('{', 5000) + new string('}', 5000)), "refuses tables nested 5000 deep (no stack overflow)");

Console.WriteLine("Lua writer (SiteData.lua is run by the game, so nothing from the site may become code)");
{
    var nasty = new JsonObject
    {
        ["\"] = os.exit() --"] = "key injection",
        ["normal"] = "\"); os.exit(); (\"",
        ["newline"] = "line1\nline2\r\n]]--",
        ["unicode"] = "Ælfrida — ★ 日本",
        ["nul"] = "a\0b",
        ["nested"] = new JsonArray(1, 2.5, true, null, new JsonObject { ["x"] = "y" }),
        ["dropped"] = null,
    };
    var lua = LuaWriter.Value(nasty);
    var back = LuaReader.ReadAssignments(Encoding.UTF8.GetBytes("X = " + lua))["X"] as JsonObject;
    nasty.Remove("dropped"); // null fields are left out, like the Python app
    Check(back != null && J(back) == J(nasty), "everything reads back exactly as it went in");
    Check(lua.Split('\n').All(l => l.Length == 0 || l.All(c => c >= 32 && c < 127)), "only printable ASCII on each line");
    JsonNode deep = new JsonArray();
    for (var i = 0; i < 100; i++) deep = new JsonArray(deep);
    Throws<InvalidDataException>(() => LuaWriter.Value(deep), "refuses data nested 100 deep");
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "perpetua-sync-writer-test.lua"), "PerpetuaSiteData = " + lua + "\n");
}

Console.WriteLine("Packed strings");
{
    var json = Encoding.UTF8.GetBytes("""{"char":{"name":"Dion"}}""");
    var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(json);
    Check((string?)AddonData.Unpack("PERP1Z:" + Convert.ToBase64String(ms.ToArray()))?["char"]?["name"] == "Dion", "PERP1Z (zlib)");
    var raw = new MemoryStream();
    using (var z = new DeflateStream(raw, CompressionLevel.Optimal, true)) z.Write(json);
    Check((string?)AddonData.Unpack("PERP1Z:" + Convert.ToBase64String(raw.ToArray()))?["char"]?["name"] == "Dion", "PERP1Z (raw deflate)");
    Check((string?)AddonData.Unpack("PERP1:" + Convert.ToBase64String(json))?["char"]?["name"] == "Dion", "PERP1 (plain)");
    Check(AddonData.Unpack("PERP1Z:not base64!") == null && AddonData.Unpack("nope") == null, "junk is ignored");
    var bomb = new MemoryStream();
    using (var z = new ZLibStream(bomb, CompressionLevel.SmallestSize, true))
    {
        var zeros = new byte[1 << 20];
        for (var i = 0; i < 100; i++) z.Write(zeros); // 100 MB of zeros, about 100 KB packed
    }
    Check(AddonData.Unpack("PERP1Z:" + Convert.ToBase64String(bomb.ToArray())) == null, "a 100 MB zip bomb is refused (cap 32 MB)");
}

Console.WriteLine("Merge");
{
    var rec = JsonNode.Parse("""
        {"profile":{"char":{"name":"Dion"},"profs":[{"n":"Tailoring","r":150}]},"pt":10,"rt":20,
         "recipes":{"Tailoring":{"list":[1,2],"t":5},"Cooking":{"list":[3],"t":6,"r":75,"m":300}}}
        """)!.AsObject();
    var m = AddonData.Merge(rec)!;
    Check(J(m["t"]) == "20", "t is the newer of pt and rt");
    Check(J(m["profs"]![0]!["recipes"]) == "[1,2]" && J(m["profs"]![0]!["seen"]) == "5", "recipes join their profession");
    Check((string?)m["profs"]![1]!["n"] == "Cooking" && J(m["profs"]![1]!["p"]) == "1", "recipes without a profession entry are added");
    Check(AddonData.Merge(JsonNode.Parse("""{"profile":{"summary":true}}""")!.AsObject()) == null, "a summary alone is skipped");
    Check(rec["profile"]!["profs"]![0]!["recipes"] == null, "the saved data isn't changed");
}

Console.WriteLine("Fingerprint");
Check(AddonData.Fingerprint(JsonNode.Parse("""{"a":1,"b":[1,{"y":2,"x":1}]}""")) ==
      AddonData.Fingerprint(JsonNode.Parse("""{"b":[1,{"x":1,"y":2}],"a":1}""")), "key order doesn't matter");

Console.WriteLine("WoW folders");
{
    var root = Path.Combine(Path.GetTempPath(), "perpetua-sync-test-" + Guid.NewGuid().ToString("N"));
    var beta = Path.Combine(root, "_classic_beta_");
    Directory.CreateDirectory(Path.Combine(beta, "Interface", "AddOns", "Perpetua"));
    Directory.CreateDirectory(Path.Combine(beta, "WTF", "Account", "ACCT", "SavedVariables"));
    File.WriteAllText(Path.Combine(beta, "WTF", "Account", "ACCT", "SavedVariables", "Perpetua.lua"), "PerpetuaDB = {}");
    Directory.CreateDirectory(Path.Combine(root, "_retail_", "WTF"));
    Directory.CreateDirectory(Path.Combine(root, "Data"));
    var f = WowFolder.Flavors(root);
    Check(f.Select(x => x.Name).SequenceEqual(["_classic_beta_", "_retail_"]), "finds every _flavor_ folder, not Data");
    Check(f[0].SavedVariables().Count() == 1 && f[0].HasAddon && !f[1].HasAddon, "saved variables and the addon folder");
    Check(WowFolder.Flavors(beta).Single().Name == "_classic_beta_", "a picked _flavor_ folder works too");
    Check(WowFolder.Flavors(Path.Combine(root, "nope")).Count == 0, "a missing folder is empty, not an error");
    Directory.Delete(root, true);
}

Console.WriteLine("Keys");
{
    var k = SiteClient.NewKey();
    Check(System.Text.RegularExpressions.Regex.IsMatch(k, "^ppsk_[A-Za-z0-9_-]{43}$"), "key format matches the site's");
    Check(SiteClient.Hash(k).Length == 64 && SiteClient.NewKey() != k, "hash is hex SHA-256; keys differ");
    Throws<ArgumentException>(() => new SiteClient(new Uri("https://evil.example/"), "1"), "no site but perpetua.gg / dev");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
