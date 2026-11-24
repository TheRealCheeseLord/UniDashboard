using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UniDashboard.Core.Web;

namespace UniDashboard.Core.Auth;

public record LoginTiming(string Method, TimeSpan Elapsed);

/// <summary>
/// Moodle mobile-token acquisition (port of MoodleAgent/tools/fetch_token.py), done with plain HTTP — no browser driver.
///
/// Token = GET launch.php?service=moodle_mobile_app WITHOUT following the redirect; the Location header
/// is moodlemobile://token=BASE64 ("signature:::token[:::privatetoken]").
///
/// Strategy, cheapest first:
///  1. Saved web session (cookies, DPAPI-encrypted): one HTTP request (~1 s).
///  2. Scripted sign-in with stored credentials (Credential Manager) — <see cref="UnivieLogin"/>. The univie IdP session cookie is
///     reused, so the SSO form is often skipped entirely; fail-fast on a bad password.
///  3. Visible browser window (App only, via <see cref="Interactive"/>) for manual login / MFA.
/// </summary>
public class AuthService(AppSettings settings, TokenStore tokens, string? sessionPath = null)
{
    readonly string _sessionPath = sessionPath ?? Path.Combine(Paths.Root, "session.bin");

    public LoginTiming? LastLogin { get; private set; }
    public string? CurrentToken() => tokens.Load();
    public void Forget() => tokens.Clear();

    /// <summary>Visible login window; set by the App. The Agent leaves it null and never shows a browser.</summary>
    public IInteractiveLogin? Interactive { get; set; }

    /// <summary>Forget the saved web session as well (used by tests / "Re-login").</summary>
    public void ForgetSession() { if (File.Exists(_sessionPath)) File.Delete(_sessionPath); }

    /// <param name="headless">Agent: true — never shows a browser. App: false — may show the login window as a last resort.</param>
    public async Task<string> LoginAsync(bool headless, CancellationToken ct = default)
    {
        // One login at a time across App and Agent (avoids racing two SSO logins / lockouts).
        await using var gate = await AcquireLockAsync(ct);
        {
            var site = settings.MoodleBaseUrl;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (await TryTokenFromSavedSessionAsync(site, ct) is { } t1) return Done(t1, "saved session", sw);

            var creds = CredentialStore.Load();
            if (creds is { } c && !IsBlocked(c))
            {
                try { return Done(await ScriptedLoginAsync(site, creds, ct), "headless sso", sw); }
                catch (BadCredentialsException) { Block(c); throw new LoginRequiredException("Stored univie credentials were rejected — update them in Settings."); }
                catch (LoginRequiredException) when (!headless) { /* fall through to the login window */ }
            }
            else if (headless)
                throw new LoginRequiredException(creds is null ? "No stored credentials for headless login" : "Stored credentials were rejected earlier — update them in Settings.");

            if (headless) throw new LoginRequiredException("Headless login did not complete (MFA required?)");
            return Done(await InteractiveLoginAsync(site, ct), "interactive", sw);
        }
    }

    /// <summary>Cross-process, await-safe lock (exclusive file handle; released automatically if the process dies).</summary>
    internal static async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(Paths.Root, "login.lock");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(300, ct); }
            catch (IOException) { throw new LoginRequiredException("Another login is in progress"); }
        }
    }

    string Done(string token, string method, System.Diagnostics.Stopwatch sw)
    {
        tokens.Save(token);
        LastLogin = new(method, sw.Elapsed);
        return token;
    }

    /// <summary>Only the university's own hosts (and the configured Moodle) are ever contacted.</summary>
    static WebSession NewSession(string site) => new([new Uri(site).Host, "univie.ac.at"]);

    static Regex MoodleDone(string site) => new($"^https://{Regex.Escape(new Uri(site).Host)}/(?!login/)");

    // ---------- 1. saved session

    async Task<string?> TryTokenFromSavedSessionAsync(string site, CancellationToken ct)
    {
        var state = LoadSession();
        if (state is null) return null;
        try
        {
            using var web = NewSession(site);
            web.Import(state);
            var token = await FetchTokenAsync(web, site, throwIfMissing: false, ct);
            if (token is not null) SaveSession(web.Export());
            else DeleteSession();   // expired — don't keep dead cookies around
            return token;
        }
        catch (Exception e) when (e is HttpRequestException or WebSecurityException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    // ---------- 2. scripted sign-in

    async Task<string> ScriptedLoginAsync(string site, (string User, string Password)? creds, CancellationToken ct)
    {
        using var web = NewSession(site);
        web.Import(LoadSession());
        var done = MoodleDone(site);
        try
        {
            // /login/index.php redirects straight to the univie Weblogin (or, if the IdP session is alive, straight back).
            await new UnivieLogin(web).RunAsync(new Uri($"{site}/login/index.php"), new(u => done.IsMatch(u.AbsoluteUri), creds), ct);
            var token = await FetchTokenAsync(web, site, throwIfMissing: true, ct) ?? throw new LoginRequiredException("No token returned");
            SaveSession(web.Export());
            return token;
        }
        catch (Exception e) when (e is HttpRequestException or WebSecurityException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new LoginRequiredException("Headless login failed: " + e.Message);
        }
    }

    // ---------- 3. visible login window

    async Task<string> InteractiveLoginAsync(string site, CancellationToken ct)
    {
        if (Interactive is null) throw new LoginRequiredException("Interactive login is not available in this process.");
        var cookies = await Interactive.RunAsync(new Uri($"{site}/login/index.php"), MoodleDone(site), ct);
        using var web = NewSession(site);
        web.Import(LoadSession());
        web.AddCookies(cookies);
        try
        {
            var token = await FetchTokenAsync(web, site, throwIfMissing: true, ct) ?? throw new LoginRequiredException("No token returned");
            SaveSession(web.Export());
            return token;
        }
        catch (Exception e) when (e is HttpRequestException or WebSecurityException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new LoginRequiredException("Could not fetch the token after login: " + e.Message);
        }
    }

    // ---------- token request (redirect NOT followed)

    static async Task<string?> FetchTokenAsync(WebSession web, string site, bool throwIfMissing, CancellationToken ct)
    {
        var passport = RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString();
        var url = $"{site}/admin/tool/mobile/launch.php?service=moodle_mobile_app&passport={passport}&urlscheme=moodlemobile";
        var resp = await web.GetAsync(new Uri(url), follow: false, ct);
        var location = resp.Location ?? "";

        if (!location.StartsWith("moodlemobile://"))
        {
            var m = Regex.Match(resp.Body, "href=\"(moodlemobile://token=[^\"]+)\"");
            if (!m.Success)
            {
                if (!throwIfMissing) return null;
                throw new LoginRequiredException($"No token redirect (HTTP {resp.Status}, Location='{Redact(location)}')");
            }
            location = WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        return ParseTokenUrl(location);
    }

    static string Redact(string s) => s.Length > 80 ? s[..80] + "…" : s;

    /// <summary>moodlemobile://token=BASE64 → token.</summary>
    public static string ParseTokenUrl(string location)
    {
        var payload = Uri.UnescapeDataString(location[(location.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Split(":::");
        if (parts.Length < 2) throw new InvalidDataException("Unexpected token payload");
        return parts[1];
    }

    // ---------- saved web session (DPAPI, current user)

    string? LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_sessionPath), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    void SaveSession(string json) =>
        File.WriteAllBytes(_sessionPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));

    void DeleteSession() { try { File.Delete(_sessionPath); } catch (IOException) { } }

    // ---------- lockout protection: never retry credentials the IdP already rejected

    internal static string CredHash((string User, string Password) c) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.User + "\0" + c.Password)));

    bool IsBlocked((string User, string Password) c) => settings.RejectedCredentialHash == CredHash(c);
    void Block((string User, string Password) c) => settings.RejectedCredentialHash = CredHash(c);
}

public class BadCredentialsException : Exception;
