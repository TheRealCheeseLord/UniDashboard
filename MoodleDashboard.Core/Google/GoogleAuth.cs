using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoodleDashboard.Core.Google;

/// <summary>OAuth client (created by the user in their own Google Cloud project) + refresh token, DPAPI-encrypted for the current Windows user.</summary>
public class GoogleCredentialStore(string? path = null)
{
    readonly string _path = path ?? Path.Combine(Paths.Root, "google.bin");

    public record Data(string ClientId = "", string ClientSecret = "", string RefreshToken = "");

    public Data Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<Data>(json) ?? new();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { return new(); }
    }

    public void Save(Data d) =>
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)), null, DataProtectionScope.CurrentUser));

    public void SaveClient(string id, string secret) => Save(Load() with { ClientId = id.Trim(), ClientSecret = secret.Trim() });
    public void SaveRefreshToken(string token) => Save(Load() with { RefreshToken = token });
    public void ClearRefreshToken() => Save(Load() with { RefreshToken = "" });
}

/// <summary>Installed-app OAuth 2.0 (loopback redirect + PKCE S256). Scopes: write events, list calendars.</summary>
public class GoogleAuth(HttpClient http, GoogleCredentialStore store)
{
    public const string Scopes = "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly";
    public string AuthEndpoint { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";
    public string RevokeEndpoint { get; init; } = "https://oauth2.googleapis.com/revoke";

    string? _accessToken;
    DateTimeOffset _expires = DateTimeOffset.MinValue;

    public bool HasClient => store.Load() is { ClientId.Length: > 0, ClientSecret.Length: > 0 };
    public bool IsConnected => store.Load().RefreshToken.Length > 0;

    // ---------- PKCE helpers (public for tests)

    public static string NewVerifier()
    {
        var b = RandomNumberGenerator.GetBytes(48);
        return Base64Url(b);                                   // 64 chars, within RFC 7636's 43–128
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string BuildAuthUrl(string clientId, string redirectUri, string state, string challenge) =>
        $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code" +
        $"&scope={Uri.EscapeDataString(Scopes)}&state={Uri.EscapeDataString(state)}&code_challenge={challenge}&code_challenge_method=S256" +
        "&access_type=offline&prompt=consent";

    // ---------- interactive sign-in

    /// <summary>Opens the system browser, waits for the loopback redirect and stores the refresh token.</summary>
    public async Task AuthorizeInteractiveAsync(Action<string>? openBrowser = null, CancellationToken ct = default)
    {
        var cfg = store.Load();
        if (cfg.ClientId.Length == 0 || cfg.ClientSecret.Length == 0) throw new InvalidOperationException("Enter the Google OAuth client id and secret first.");

        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/";
        var verifier = NewVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(18));
        var url = BuildAuthUrl(cfg.ClientId, redirect, state, Challenge(verifier));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        (openBrowser ?? (u => Process.Start(new ProcessStartInfo(u) { UseShellExecute = true })))(url);

        var ctxTask = listener.GetContextAsync();
        var done = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (done != ctxTask) throw new TimeoutException("Google sign-in was not completed within 5 minutes.");
        var ctx = await ctxTask;

        var q = ctx.Request.QueryString;
        string? error = q["error"], code = q["code"];
        var ok = error is null && code is not null && q["state"] == state;
        var page = Encoding.UTF8.GetBytes(ok
            ? "<html><body style='font-family:sans-serif'><h3>Connected to Google Calendar.</h3>You can close this tab and return to MoodleDashboard.</body></html>"
            : "<html><body style='font-family:sans-serif'><h3>Sign-in failed.</h3>Please return to MoodleDashboard and try again.</body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(page, ct);
        ctx.Response.Close();
        listener.Stop();

        if (!ok) throw new GoogleAuthRequiredException(error is not null ? $"Google sign-in was denied ({error})." : "Google sign-in returned an unexpected response.");
        await ExchangeCodeAsync(code!, redirect, verifier, ct);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task ExchangeCodeAsync(string code, string redirectUri, string verifier, CancellationToken ct = default)
    {
        var cfg = store.Load();
        using var doc = await PostTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = cfg.ClientId, ["client_secret"] = cfg.ClientSecret,
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
        }, ct);
        var r = doc.RootElement;
        if (!r.TryGetProperty("refresh_token", out var rt) || string.IsNullOrEmpty(rt.GetString()))
            throw new GoogleAuthRequiredException("Google did not return a refresh token. Remove the app's access in your Google account and connect again.");
        store.SaveRefreshToken(rt.GetString()!);
        Remember(r);
    }

    // ---------- tokens

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expires.AddSeconds(-60)) return _accessToken;
        var cfg = store.Load();
        if (cfg.RefreshToken.Length == 0) throw new GoogleAuthRequiredException("Not connected to Google Calendar.");

        using var doc = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId, ["client_secret"] = cfg.ClientSecret, ["refresh_token"] = cfg.RefreshToken, ["grant_type"] = "refresh_token",
        }, ct);
        Remember(doc.RootElement);
        return _accessToken!;
    }

    void Remember(JsonElement r)
    {
        _accessToken = r.GetProperty("access_token").GetString();
        _expires = DateTimeOffset.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3000);
    }

    async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body);

        string error = "", description = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            error = d.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            description = d.RootElement.TryGetProperty("error_description", out var ed) ? ed.GetString() ?? "" : "";
        }
        catch (JsonException) { }

        if (error == "invalid_grant")
        {
            // Revoked, expired (Testing-mode projects expire refresh tokens after 7 days) or password changed.
            store.ClearRefreshToken();
            _accessToken = null;
            throw new GoogleAuthRequiredException("Google access expired or was revoked — please connect again.");
        }
        throw new GoogleApiException((int)resp.StatusCode, $"Google token endpoint: {error} {description}".Trim());
    }

    /// <summary>Forgets the tokens; also asks Google to revoke the refresh token (best effort).</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var rt = store.Load().RefreshToken;
        store.ClearRefreshToken();
        _accessToken = null;
        if (rt.Length == 0) return;
        try { using var _ = await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = rt }), ct); }
        catch (HttpRequestException) { }
    }
}
