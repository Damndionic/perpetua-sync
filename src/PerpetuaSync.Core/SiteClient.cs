using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PerpetuaSync.Core;

/// <summary>The site said no: Status is the HTTP status, Code the site's error code ("token", "not_officer", …).</summary>
public sealed class SiteException(HttpStatusCode status, string code) : Exception($"{(int)status} {code}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// The only network the app does: HTTPS to the guild site, nothing else. The address is fixed in the app (no
/// setting can point it elsewhere), redirects are refused, certificates are checked by Windows as usual, and
/// replies are size-capped.
/// </summary>
public sealed class SiteClient : IDisposable
{
    public static readonly Uri Live = new("https://perpetua.gg/");
    public static readonly Uri Dev = new("https://dev.perpetua.gg/");

    readonly HttpClient _http;
    public Uri Site { get; }
    public string? Key { get; set; }

    public SiteClient(Uri site, string version)
    {
        if (site != Live && site != Dev) throw new ArgumentException("unknown site", nameof(site));
        Site = site;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            BaseAddress = site,
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = 32 * 1024 * 1024,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PerpetuaSync-Windows", version));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A new key: "ppsk_" + 32 random bytes. The app keeps it; the site only ever gets its hash at pairing.</summary>
    public static string NewKey() =>
        "ppsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<JsonObject> Send(HttpMethod method, string path, JsonNode? body = null, bool auth = true, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (auth)
        {
            if (Key == null) throw new SiteException(HttpStatusCode.Unauthorized, "token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonObject? data = null;
        try { data = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
        if (!res.IsSuccessStatusCode || data == null || data["ok"]?.GetValue<bool>() == false)
        {
            var code = (data?["error"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : res.StatusCode.ToString();
            throw new SiteException(res.StatusCode, code);
        }
        return data;
    }

    public Task<JsonObject> Get(string path, CancellationToken ct = default) => Send(HttpMethod.Get, path, null, true, ct);
    public Task<JsonObject> Post(string path, JsonNode body, CancellationToken ct = default) => Send(HttpMethod.Post, path, body, true, ct);
}
