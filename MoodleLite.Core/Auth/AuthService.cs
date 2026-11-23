using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace MoodleLite.Core.Auth;

public record LoginTiming(string Method, TimeSpan Elapsed);

/// <summary>
/// Moodle mobile-token acquisition (port of MoodleAgent/tools/fetch_token.py, hardened and sped up).
///
/// Token = GET launch.php?service=moodle_mobile_app WITHOUT following the redirect; the Location header
/// is moodlemobile://token=BASE64 ("signature:::token[:::privatetoken]").
///
/// Strategy, cheapest first:
///  1. Saved web session (cookies, DPAPI-encrypted): plain HTTP request, no browser at all (~1 s).
///  2. Headless Chrome with stored credentials (Credential Manager). The univie IdP session cookie is reused,
///     so the SSO form is often skipped entirely; images/css/fonts are blocked; fail-fast on bad password.
///  3. Visible browser (App only) for manual login / MFA.
/// </summary>
public class AuthService(AppSettings settings, TokenStore tokens, string? sessionPath = null)
{
    readonly string _sessionPath = sessionPath ?? Path.Combine(Paths.Root, "session.bin");

    // Protocol-level Shibboleth names first (stable across IdP re-designs), then the ids used today.
    const string UserSel = "input[name='j_username'], input#userid, input[autocomplete='username'], input[type='text']";
    const string PassSel = "input[name='j_password'], input#password, input[type='password']";
    const string SubmitSel = "button[name='_eventId_proceed'], form button[type='submit'], form input[type='submit']";
    const string ConsentSel = "button[name='_eventId_proceed'], input[value*='Confirm'], input[value*='Akzeptieren']";

    public LoginTiming? LastLogin { get; private set; }
    public string? CurrentToken() => tokens.Load();
    public void Forget() => tokens.Clear();

    /// <summary>Forget the saved web session as well (used by tests / "Re-login").</summary>
    public void ForgetSession() { if (File.Exists(_sessionPath)) File.Delete(_sessionPath); }

    /// <param name="headless">Agent: true — never shows a browser. App: false — may show Chrome as a last resort.</param>
    public async Task<string> LoginAsync(bool headless, CancellationToken ct = default)
    {
        // One login at a time across App and Agent (avoids racing two SSO logins / lockouts).
        await using var gate = await AcquireLockAsync(ct);
        {
            // Someone else may have just logged in while we waited.
            var site = settings.MoodleBaseUrl;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (await TryTokenFromSavedSessionAsync(site, ct) is { } t1) return Done(t1, "saved session", sw);

            var creds = CredentialStore.Load();
            if (creds is { } c && !IsBlocked(c))
            {
                try { return Done(await BrowserLoginAsync(site, creds, headlessMode: true, ct), "headless sso", sw); }
                catch (BadCredentialsException) { Block(c); throw new LoginRequiredException("Stored univie credentials were rejected — update them in Settings."); }
                catch (LoginRequiredException) when (!headless) { /* fall through to a visible window */ }
            }
            else if (headless)
                throw new LoginRequiredException(creds is null ? "No stored credentials for headless login" : "Stored credentials were rejected earlier — update them in Settings.");

            if (headless) throw new LoginRequiredException("Headless login did not complete (MFA required?)");
            return Done(await BrowserLoginAsync(site, creds, headlessMode: false, ct), "interactive", sw);
        }
    }

    /// <summary>Cross-process, await-safe lock (exclusive file handle; released automatically if the process dies).</summary>
    static async Task<FileStream> AcquireLockAsync(CancellationToken ct)
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

    // ---------- 1. saved session, no browser

    async Task<string?> TryTokenFromSavedSessionAsync(string site, CancellationToken ct)
    {
        var state = LoadSession();
        if (state is null) return null;
        try
        {
            using var pw = await Playwright.CreateAsync();
            var api = await pw.APIRequest.NewContextAsync(new() { StorageState = state, IgnoreHTTPSErrors = false, Timeout = 20_000 });
            try
            {
                var token = await FetchTokenAsync(api, site, throwIfMissing: false);
                if (token is not null) SaveSession(await api.StorageStateAsync());
                else DeleteSession();   // expired — don't keep dead cookies around
                return token;
            }
            finally { await api.DisposeAsync(); }
        }
        catch (PlaywrightException) { return null; }
    }

    // ---------- 2./3. browser login

    async Task<string> BrowserLoginAsync(string site, (string User, string Password)? creds, bool headlessMode, CancellationToken ct)
    {
        var host = new Uri(site).Host;
        using var pw = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(pw, headlessMode);
        var context = await browser.NewContextAsync(new() { StorageState = LoadSession(), Locale = "de-AT" });
        context.SetDefaultTimeout(headlessMode ? 20_000 : 300_000);
        var page = await context.NewPageAsync();

        if (headlessMode)
        {
            // Nobody looks at the pages: skip heavy resources.
            await page.RouteAsync("**/*", r =>
                r.Request.ResourceType is "image" or "font" or "stylesheet" or "media" ? r.AbortAsync() : r.ContinueAsync());
        }

        var moodleRe = new Regex($"^https://{Regex.Escape(host)}/(?!login/)");
        // /login/index.php redirects straight to the univie Weblogin (or, if the IdP session is alive, straight back).
        await page.GotoAsync($"{site}/login/index.php", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        if (!moodleRe.IsMatch(page.Url))
        {
            if (creds is { } c) await SubmitCredentialsAsync(page, c, moodleRe);
            // else: interactive — the user types in the visible window
            try { await page.WaitForURLAsync(moodleRe, new() { Timeout = headlessMode ? 20_000 : 300_000, WaitUntil = WaitUntilState.Commit }); }
            catch (TimeoutException) { throw new LoginRequiredException(headlessMode ? "Headless login did not complete (MFA required?)" : "Login timed out"); }
        }

        var token = await FetchTokenAsync(context.APIRequest, site, throwIfMissing: true) ?? throw new LoginRequiredException("No token returned");
        SaveSession(await context.StorageStateAsync());
        return token;
    }

    static async Task SubmitCredentialsAsync(IPage page, (string User, string Password) c, Regex moodleRe)
    {
        var userBox = page.Locator(UserSel).First;
        try { await userBox.WaitForAsync(new() { Timeout = 8_000 }); }
        catch (TimeoutException)
        {
            // No form: maybe the IdP is redirecting back on its own, or a consent screen is shown.
            await TryConsentAsync(page);
            return;
        }
        await userBox.FillAsync(c.User);
        await page.Locator(PassSel).First.FillAsync(c.Password);
        await page.Locator(SubmitSel).First.ClickAsync();

        // Race: success (back on Moodle) / consent page / the login form re-rendered with an error.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (moodleRe.IsMatch(page.Url)) return;
            if (await TryConsentAsync(page)) { continue; }
            // Still on the form with the password field present and an error marker → wrong password.
            if (page.Url.Contains("weblogin") && await page.Locator(".form-error, .output--error, .alert-danger, .error").CountAsync() > 0)
                throw new BadCredentialsException();
            await Task.Delay(150);
        }
    }

    static async Task<bool> TryConsentAsync(IPage page)
    {
        var btn = page.Locator(ConsentSel).First;
        if (await page.Locator(PassSel).CountAsync() > 0) return false;  // that's the login form, not a consent page
        if (await btn.CountAsync() == 0) return false;
        try { await btn.ClickAsync(new() { Timeout = 3_000 }); return true; }
        catch (PlaywrightException) { return false; }
    }

    static async Task<IBrowser> LaunchAsync(IPlaywright pw, bool headless)
    {
        var opts = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = ["--disable-extensions", "--disable-background-networking", "--no-first-run", "--disable-gpu"],
        };
        // Prefer Chrome, fall back to Edge (both already installed; no download).
        try { return await pw.Chromium.LaunchAsync(new(opts) { Channel = "chrome" }); }
        catch (PlaywrightException) { return await pw.Chromium.LaunchAsync(new(opts) { Channel = "msedge" }); }
    }

    // ---------- token request (redirect NOT followed)

    static async Task<string?> FetchTokenAsync(IAPIRequestContext api, string site, bool throwIfMissing)
    {
        var passport = RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString();
        var url = $"{site}/admin/tool/mobile/launch.php?service=moodle_mobile_app&passport={passport}&urlscheme=moodlemobile";
        var resp = await api.GetAsync(url, new() { MaxRedirects = 0, Timeout = 20_000 });
        var location = resp.Headers.TryGetValue("location", out var l) ? l : "";

        if (!location.StartsWith("moodlemobile://"))
        {
            var m = Regex.Match(await resp.TextAsync(), "href=\"(moodlemobile://token=[^\"]+)\"");
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

    static string CredHash((string User, string Password) c) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.User + "\0" + c.Password)));

    bool IsBlocked((string User, string Password) c) => settings.RejectedCredentialHash == CredHash(c);
    void Block((string User, string Password) c) => settings.RejectedCredentialHash = CredHash(c);
}

public class BadCredentialsException : Exception;
