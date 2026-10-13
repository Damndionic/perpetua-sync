using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PerpetuaSync.Core;

/// <summary>A newer version the site says is current: its number and the SHA-256 its exe must have.</summary>
public sealed record Release(Version Version, string Sha256);

/// <summary>
/// Updates, two sources that must agree. perpetua.gg says which version is current and what its exe's SHA-256 is;
/// the exe itself comes only from this project's GitHub releases (the address is built here from the version number,
/// never taken from the site). A file whose hash doesn't match is deleted, never run. Never goes to an older version.
/// </summary>
public static partial class Updates
{
    const string Repo = "https://github.com/Damndionic/perpetua-sync/releases/download/";
    // Where GitHub serves release files from (github.com redirects there). Any other host is refused.
    static readonly string[] Hosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    const long MaxBytes = 150L * 1024 * 1024;

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionFormat();
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashFormat();

    /// <summary>The site's current release if it's newer than this one, else null.</summary>
    public static async Task<Release?> Check(SiteClient site, Version current, CancellationToken ct = default)
    {
        var r = await site.Send(HttpMethod.Get, "/api/sync/app", auth: false, ct: ct);
        var v = (string?)r["version"];
        var h = ((string?)r["sha256"])?.ToLowerInvariant();
        if (v == null || h == null || !VersionFormat().IsMatch(v) || !HashFormat().IsMatch(h)) return null;
        var version = Version.Parse(v);
        return version > current ? new Release(version, h) : null;
    }

    public static Uri DownloadUrl(Version v) => new($"{Repo}v{v.Major}.{v.Minor}.{v.Build}/PerpetuaSync.exe");

    /// <summary>Downloads the release to <paramref name="target"/> and checks its SHA-256. Throws (and leaves no file)
    /// if anything is off.</summary>
    public static async Task Download(Release release, string target, string userAgent, CancellationToken ct = default)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        var url = DownloadUrl(release.Version);
        var tmp = target + ".part";
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (url.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException($"update download went to {url.Host}, which isn't GitHub; stopped");
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode is HttpStatusCode.Found or HttpStatusCode.Moved or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
                {
                    if (hop >= 5 || res.Headers.Location == null) throw new InvalidDataException("too many redirects");
                    url = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(url, res.Headers.Location);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("update is unexpectedly large");
                await using (var input = await res.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(tmp))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await input.ReadAsync(buf, ct)) > 0)
                    {
                        total += n;
                        if (total > MaxBytes) throw new InvalidDataException("update is unexpectedly large");
                        await output.WriteAsync(buf.AsMemory(0, n), ct);
                    }
                }
                break;
            }
            string got;
            await using (var f = File.OpenRead(tmp)) got = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (got != release.Sha256) throw new InvalidDataException($"update failed its check (SHA-256 {got[..12]}…, expected {release.Sha256[..12]}…); not installed");
            File.Move(tmp, target, true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
