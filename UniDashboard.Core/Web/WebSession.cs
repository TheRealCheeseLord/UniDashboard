using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UniDashboard.Core.Web;

/// <summary>A request was refused by the session's safety rules (non-https, host outside the allow-list, redirect loop).</summary>
public class WebSecurityException(string message) : Exception(message);

public sealed record WebResponse(Uri Url, int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Raw <c>Location</c> header (may be a non-http scheme such as <c>moodlemobile://</c>).</summary>
    public string? Location => Headers.TryGetValue("location", out var l) ? l : null;
    public bool IsRedirect => Status is 301 or 302 or 303 or 307 or 308;
}

/// <summary>A cookie as exchanged with an embedded browser or stored on disk.</summary>
public sealed record StoredCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, DateTimeOffset? Expires);

/// <summary>
/// Purpose-built replacement for a browser driver: an HTTP client with its own cookie jar, used for the university sign-in
/// and the Moodle / u:space calls. Deliberately small and strict:
/// <list type="bullet">
/// <item>https only, and only hosts inside the allow-list (suffix match) are ever contacted — also for every redirect hop;</item>
/// <item>redirects are followed manually (bounded), so credentials can never be sent somewhere a redirect pointed us;</item>
/// <item>response bodies are capped; cookies are only accepted from allow-listed hosts (we never connect elsewhere).</item>
/// </list>
/// Cookie persistence uses the Playwright <c>storageState</c> JSON shape (<c>{"cookies":[…]}</c>), so sessions saved by earlier
/// versions of the app load unchanged.
/// </summary>
public sealed class WebSession : IDisposable
{
    public const int MaxRedirects = 15;
    const int MaxBodyBytes = 4 * 1024 * 1024;
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 UniDashboard";

    readonly HttpClient _http;
    readonly CookieContainer _cookies = new();
    readonly string[] _allowed;

    /// <summary>Optional diagnostic sink. Receives hosts, paths (no query strings) and status codes only — never cookies, bodies or form values.</summary>
    public Action<string>? Trace { get; set; }

    public WebSession(IEnumerable<string> allowedHostSuffixes, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _allowed = allowedHostSuffixes.Select(s => s.Trim().TrimStart('.').ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToArray();
        if (_allowed.Length == 0) throw new ArgumentException("At least one allowed host is required.", nameof(allowedHostSuffixes));
        handler ??= new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _http.Dispose();

    public bool IsAllowed(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return false;
        var host = url.IdnHost.ToLowerInvariant();
        return _allowed.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal));
    }

    // ---------- requests

    public Task<WebResponse> GetAsync(Uri url, bool follow = true, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, url, follow: follow, ct: ct);

    public Task<WebResponse> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, bool follow = true, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, url, form: fields, follow: follow, ct: ct);

    public Task<WebResponse> PostJsonAsync(Uri url, string json, IDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, url, body: json, contentType: "application/json", headers: headers, follow: false, ct: ct);

    public async Task<WebResponse> SendAsync(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>>? form = null, string? body = null,
        string? contentType = null, IDictionary<string, string>? headers = null, bool follow = true, CancellationToken ct = default)
    {
        for (var hop = 0; ; hop++)
        {
            if (hop > MaxRedirects) throw new WebSecurityException("Too many redirects.");
            if (!IsAllowed(url)) throw new WebSecurityException($"Blocked request to {Describe(url)} (only https hosts of the university are allowed).");

            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
            req.Headers.TryAddWithoutValidation("Accept-Language", "de-AT,de;q=0.9,en;q=0.8");
            var cookie = _cookies.GetCookieHeader(url);
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (headers is not null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            if (form is not null) req.Content = new FormUrlEncodedContent(form);
            else if (body is not null)
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            StoreCookies(url, resp);
            var result = new WebResponse(url, (int)resp.StatusCode, Flatten(resp), await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false));
            Trace?.Invoke($"{method} {Describe(url)} -> {result.Status}" + (result.IsRedirect && result.Location is { } l ? " -> " + DescribeLocation(url, l) : ""));

            if (!follow || !result.IsRedirect || result.Location is not { } loc) return result;
            if (!Uri.TryCreate(url, loc, out var next) || next.Scheme is not ("http" or "https")) return result;   // e.g. moodlemobile://token=…
            if (result.Status == 303 || (result.Status is 301 or 302 && method == HttpMethod.Post)) { method = HttpMethod.Get; form = null; body = null; }
            url = next;
        }
    }

    static string Describe(Uri u) => u.IdnHost + u.AbsolutePath;
    static string DescribeLocation(Uri baseUrl, string loc) => Uri.TryCreate(baseUrl, loc, out var u) ? (u.Scheme is "http" or "https" ? Describe(u) : u.Scheme + "://…") : "?";

    static Dictionary<string, string> Flatten(HttpResponseMessage resp)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
            if (!h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) d[h.Key] = string.Join(",", h.Value);
        return d;
    }

    static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBodyBytes) throw new WebSecurityException("Response too large.");
        }
        Encoding enc = Encoding.UTF8;
        try { if (content.Headers.ContentType?.CharSet is { Length: > 0 } cs) enc = Encoding.GetEncoding(cs.Trim('"')); } catch (ArgumentException) { }
        return enc.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ---------- cookies

    void StoreCookies(Uri url, HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var v in values)
            try { _cookies.SetCookies(url, v); } catch (CookieException) { /* malformed: ignore, like a browser */ }
    }

    public IReadOnlyList<StoredCookie> Cookies() =>
        _cookies.GetAllCookies().Where(c => !c.Expired)
            .Select(c => new StoredCookie(c.Name, c.Value, c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure, c.HttpOnly,
                c.Expires == default ? null : new DateTimeOffset(c.Expires.ToUniversalTime()))).ToList();

    public void AddCookies(IEnumerable<StoredCookie> cookies)
    {
        foreach (var c in cookies)
        {
            if (c.Expires is { } e && e <= DateTimeOffset.UtcNow) continue;
            try
            {
                var cookie = new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly };
                if (c.Expires is { } exp) cookie.Expires = exp.UtcDateTime;
                _cookies.Add(cookie);
            }
            catch (CookieException) { }
            catch (ArgumentException) { }
        }
    }

    /// <summary>Serialises the jar in the Playwright <c>storageState</c> shape (cookies only).</summary>
    public string Export() => JsonSerializer.Serialize(new
    {
        cookies = Cookies().Select(c => new
        {
            name = c.Name, value = c.Value, domain = c.Domain, path = c.Path,
            expires = c.Expires is { } e ? e.ToUnixTimeSeconds() : -1L,
            httpOnly = c.HttpOnly, secure = c.Secure, sameSite = "Lax",
        }),
        origins = Array.Empty<object>(),
    });

    /// <summary>Loads cookies from <see cref="Export"/> output or from a session file written by earlier (Playwright) versions.</summary>
    public void Import(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cookies", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var list = new List<StoredCookie>();
            foreach (var c in arr.EnumerateArray())
            {
                string S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool B(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                var name = S("name"); var domain = S("domain");
                if (name.Length == 0 || domain.Length == 0) continue;
                DateTimeOffset? exp = c.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetDouble(out var secs) && secs > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)secs) : null;
                list.Add(new(name, S("value"), domain, S("path"), B("secure"), B("httpOnly"), exp));
            }
            AddCookies(list);
        }
        catch (JsonException) { /* corrupt file: behave as "no session" */ }
    }
}
