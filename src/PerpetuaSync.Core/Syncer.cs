using System.Net;
using System.Text.Json.Nodes;

namespace PerpetuaSync.Core;

/// <summary>
/// Uploads what the addon collected and writes the site's calendar and forums into the addon's SiteData.lua.
/// A port of sync/perpetua_sync.py (sync, sync_loot, sync_roster, sync_calendar); keep the two in step.
///
/// What it touches on disk: it reads Perpetua.lua under each game folder's WTF, and writes exactly one file,
/// Interface\AddOns\Perpetua\SiteData.lua, only where the Perpetua addon folder already exists. Its own state is a
/// JSON file in the app's folder under %LOCALAPPDATA%.
/// </summary>
public sealed class Syncer(SiteClient site, string stateFile, Action<string> log)
{
    const int Batch = 50;
    static readonly TimeSpan RetrySkipped = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CalendarEvery = TimeSpan.FromMinutes(5);
    const long MaxSiteDataBytes = 8 * 1024 * 1024;

    JsonObject _state = LoadState(stateFile);
    public bool DryRun { get; set; }

    static JsonObject LoadState(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var tmp = stateFile + ".tmp";
        File.WriteAllText(tmp, _state.ToJsonString());
        File.Move(tmp, stateFile, true);
    }

    /// <summary>Forget what was uploaded (after connecting as someone else, say), so everything goes up again.</summary>
    public void ResetState() { _state = []; SaveState(); }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    List<(string Path, JsonObject Db)> Read(IEnumerable<string> files)
    {
        var dbs = new List<(string, JsonObject)>();
        foreach (var f in files)
        {
            try { dbs.Add((f, AddonData.ReadDb(f))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
            {
                // Half-written (WoW is saving right now) or a format we don't know: try again next time.
                log($"Couldn't read {Short(f)}: {e.Message}");
            }
        }
        return dbs;
    }

    static string Short(string f)
    {
        // ...\_classic_beta_\WTF\Account\NAME\SavedVariables\Perpetua.lua -> _classic_beta_ (NAME)
        var p = f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Length >= 6 ? $"{p[^6]} ({p[^3]})" : Path.GetFileName(f);
    }

    /// <summary>Uploads: loot and rolls, the guild roster, then every profile that changed.</summary>
    public async Task Upload(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var dbs = Read(files);
        try { await UploadLoot(dbs, ct); }
        catch (SiteException e) when (e.Status == HttpStatusCode.NotFound) { } // the site has raids switched off
        try { await UploadRoster(dbs, ct); }
        catch (SiteException e) when (e.Status == HttpStatusCode.NotFound) { }
        await UploadProfiles(dbs, ct);
    }

    async Task UploadLoot(List<(string, JsonObject)> dbs, CancellationToken ct)
    {
        // Newer than the last upload, with ten minutes' overlap (the site drops repeats).
        var lastT = AddonData.Long(_state["_loot_t"]);
        var since = lastT - 600;
        var (loot, rolls) = AddonData.CollectLoot(dbs);
        loot = loot.Where(x => AddonData.Long(x["t"]) > since).ToList();
        rolls = rolls.Where(x => AddonData.Long(x["t"]) > since).ToList();
        if (loot.Count == 0 && rolls.Count == 0) return;
        if (DryRun) { log($"Would upload {loot.Count} drops and {rolls.Count} rolls"); return; }
        var body = new JsonObject
        {
            ["loot"] = new JsonArray(loot.TakeLast(500).Select(x => (JsonNode)x.DeepClone()).ToArray()),
            ["rolls"] = new JsonArray(rolls.TakeLast(2000).Select(x => (JsonNode)x.DeepClone()).ToArray()),
        };
        var res = await site.Post("/api/sync/loot", body, ct);
        _state["_loot_t"] = loot.Concat(rolls).Select(x => AddonData.Long(x["t"])).Append(lastT).Max();
        SaveState();
        var newLoot = AddonData.Long(res["loot"]);
        var newRolls = AddonData.Long(res["rolls"]);
        if (newLoot > 0 || newRolls > 0) log($"Loot history: {newLoot} new drops, {newRolls} new rolls");
    }

    async Task UploadRoster(List<(string, JsonObject)> dbs, CancellationToken ct)
    {
        var (at, roster) = AddonData.CollectRoster(dbs);
        if (roster.Count == 0) return;
        var members = AddonData.RosterMembers(roster);
        var fp = AddonData.Fingerprint(members);
        if ((string?)_state["_roster_fp"] == fp) return;
        if (DryRun) { log($"Would upload the guild roster: {members.Count} members"); return; }
        var res = await site.Post("/api/sync/roster", new JsonObject { ["at"] = at, ["members"] = members }, ct);
        _state["_roster_fp"] = fp;
        SaveState();
        if (res["raised"] is JsonArray raised && raised.Count > 0)
            log("Levels from the guild roster: " + string.Join(", ", raised.Select(x => x?.ToString())));
    }

    static readonly Dictionary<string, string> Labels = new()
    {
        ["saved"] = "updated", ["matched"] = "matched to a roster entry", ["added"] = "added to their owner as alts",
        ["linked"] = "linked to a Discord account", ["unloaded"] = "skipped (not fully loaded)",
        ["stale"] = "already newer on the site", ["not_on_roster"] = "not on the roster",
        ["ambiguous"] = "ambiguous (two entries with that name and class)", ["invalid"] = "unreadable",
        ["name_blocked"] = "refused by the site's name filter", ["beta"] = "from the beta",
    };

    async Task UploadProfiles(List<(string, JsonObject)> dbs, CancellationToken ct)
    {
        var found = AddonData.Collect(dbs);
        var now = Now;
        var todo = new List<(string Name, string Fp, JsonObject Data)>();
        foreach (var (name, p) in found.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var fp = AddonData.Fingerprint(p.Data);
            var s = _state[name] as JsonObject;
            var at = s?["at"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            if ((string?)s?["fp"] == fp && ((string?)s?["result"] != "not_on_roster" || now - at < RetrySkipped.TotalSeconds)) continue;
            todo.Add((name, fp, p.Data));
        }
        if (todo.Count == 0) return;
        if (DryRun)
        {
            foreach (var (name, _, data) in todo)
                log($"Would upload {name} (level {data["char"]?["level"]}, {(data["gear"] as JsonArray)?.Count ?? 0} items)");
            return;
        }
        var counts = new Dictionary<string, List<string>>();
        var alts = new List<string>();
        var made = new List<string>();
        for (var i = 0; i < todo.Count; i += Batch)
        {
            var batch = todo.Skip(i).Take(Batch).ToList();
            var res = await site.Post("/api/sync/profiles",
                new JsonObject { ["profiles"] = new JsonArray(batch.Select(b => (JsonNode)b.Data.DeepClone()).ToArray()) }, ct);
            var results = res["results"] as JsonArray ?? [];
            for (var k = 0; k < batch.Count && k < results.Count; k++)
            {
                var (name, fp, _) = batch[k];
                var r = results[k] as JsonObject ?? [];
                var shown = (string?)r["name"] ?? name;
                var result = (string?)r["result"] ?? "?";
                (counts.TryGetValue(result, out var l) ? l : counts[result] = []).Add(shown);
                if (r["alts"] is JsonArray a) alts.AddRange(a.Select(x => x?.ToString() ?? ""));
                if (r["raids"] is JsonObject raids)
                {
                    if (AddonData.Long(raids["made"]) > 0) made.Add($"{raids["made"]} from {shown}");
                    if (AddonData.Long(raids["refused"]) > 0)
                        log($"{shown} scheduled {raids["refused"]} raid(s) in game, but their Discord account isn't an officer: refused");
                }
                _state[name] = new JsonObject { ["fp"] = fp, ["result"] = result, ["at"] = now };
            }
        }
        SaveState();
        var parts = counts.Select(c => $"{c.Value.Count} {Labels.GetValueOrDefault(c.Key, c.Key)}: {string.Join(", ", c.Value)}").ToList();
        if (alts.Count > 0) parts.Add($"{alts.Count} alts added from the same accounts: {string.Join(", ", alts)}");
        if (made.Count > 0) parts.Add($"raids scheduled in game: {string.Join(", ", made)}");
        log("Profiles: " + string.Join(" · ", parts));
    }

    /// <summary>The raid calendar and forums into the addon's SiteData.lua (PerpetuaSiteData / PerpetuaSiteForums), for
    /// each game folder that has the addon. Unchanged content isn't rewritten.</summary>
    public async Task Download(IReadOnlyList<Flavor> flavors, CancellationToken ct = default)
    {
        var cal = await Optional("/api/sync/calendar", ct);
        var forums = await Optional("/api/sync/forums", ct);
        if (cal == null && forums == null) return;
        JsonObject? calBody = cal == null ? null : new JsonObject
        {
            ["t"] = cal["t"]?.DeepClone(), ["events"] = cal["events"]?.DeepClone() ?? new JsonArray(),
            ["roster"] = cal["roster"]?.DeepClone() ?? new JsonArray(),
        };
        JsonObject? forumBody = forums == null ? null : new JsonObject
        {
            ["t"] = forums["t"]?.DeepClone(), ["boards"] = forums["boards"]?.DeepClone(), ["threads"] = forums["threads"]?.DeepClone(),
        };
        // Compare without the time stamps, so an unchanged calendar isn't rewritten (and re-shared in game) each time.
        var contentFp = AddonData.Fingerprint(new JsonObject
        {
            ["events"] = calBody?["events"]?.DeepClone(), ["roster"] = calBody?["roster"]?.DeepClone(),
            ["boards"] = forumBody?["boards"]?.DeepClone(), ["threads"] = forumBody?["threads"]?.DeepClone(),
        });
        var text = "-- The raid calendar and forums from the site, written by the Perpetua app on " +
                   DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".\n-- The game loads it at login and /reload; the addon shares it with the guild.\n" +
                   "PerpetuaSiteData = " + LuaWriter.Value(calBody) + "\n" +
                   "PerpetuaSiteForums = " + LuaWriter.Value(forumBody) + "\n";
        if (text.Length > MaxSiteDataBytes) throw new InvalidDataException("the site's calendar is unexpectedly large; not written");

        var fps = _state["_calendar_fp"] as JsonObject ?? [];
        _state["_calendar_fp"] = fps;
        foreach (var f in flavors)
        {
            if (!f.HasAddon) continue;
            var target = Path.Combine(f.AddonDir, "SiteData.lua");
            if ((string?)fps[target] == contentFp && File.Exists(target)) continue;
            if (DryRun) { log($"Would write the calendar into {f.Name}"); continue; }
            WriteSiteData(f.AddonDir, target, text);
            fps[target] = contentFp;
            SaveState();
            var parts = new List<string>();
            if (cal != null) parts.Add($"{(cal["events"] as JsonArray)?.Count ?? 0} raids");
            if (forums != null) parts.Add($"{(forums["threads"] as JsonArray)?.Count ?? 0} forum threads");
            log($"Calendar for the addon: {string.Join(" and ", parts)} ({f.Name}). /reload in game to see changes.");
        }
    }

    // Writes next to the target and swaps it in, so the game never loads half a file. Refuses links (a symlink or
    // junction in the addon folder could point the write anywhere).
    static void WriteSiteData(string addonDir, string target, string text)
    {
        if (new DirectoryInfo(addonDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("the Perpetua addon folder is a link; not writing into it");
        if (File.Exists(target) && new FileInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("SiteData.lua is a link; not replacing it");
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, target, true);
    }

    async Task<JsonObject?> Optional(string path, CancellationToken ct)
    {
        try { return await site.Get(path, ct); }
        catch (SiteException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }
}
