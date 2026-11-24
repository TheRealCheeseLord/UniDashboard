using System.Text.RegularExpressions;
using UniDashboard.Core.Auth;

namespace UniDashboard.Core.Web;

/// <summary>
/// A visible browser window for the cases a script cannot handle (first login, MFA, changed login pages). Implemented by the app
/// (WebView2); the background agent has none. Shows <paramref name="start"/>, waits until the browser reaches an address matching
/// <paramref name="done"/>, and returns the session cookies of that browser.
/// </summary>
public interface IInteractiveLogin
{
    Task<IReadOnlyList<StoredCookie>> RunAsync(Uri start, Regex done, CancellationToken ct);
}

/// <summary>
/// Scripted university sign-in over plain HTTP (no browser): follows the redirect chain and does what a browser would do on each
/// page — fills the Shibboleth login form, submits the SAML hand-over form that a browser auto-posts with JavaScript, accepts the
/// attribute-release page, and (u:space) picks the Shibboleth identity provider on the Keycloak page. Field names are protocol-level
/// names (<c>j_username</c>, <c>SAMLResponse</c>, <c>_eventId_proceed</c>) rather than page layout, so it survives redesigns.
/// </summary>
public sealed class UnivieLogin(WebSession web)
{
    public const int MaxSteps = 14;

    public sealed record Options(Func<Uri, bool> IsDone, (string User, string Password)? Credentials = null, string[]? LinkHints = null);

    static readonly string[] UserFieldNames = ["j_username", "username", "userid", "user", "login", "email"];

    /// <exception cref="BadCredentialsException">The login form came back after the credentials were submitted.</exception>
    /// <exception cref="LoginRequiredException">The flow needs something a script cannot do (no credentials, MFA, unknown page).</exception>
    public async Task<WebResponse> RunAsync(Uri start, Options o, CancellationToken ct = default)
    {
        var resp = await web.GetAsync(start, ct: ct).ConfigureAwait(false);
        var submitted = false;
        var seen = new Dictionary<string, int>();

        for (var step = 0; step < MaxSteps; step++)
        {
            if (o.IsDone(resp.Url)) return resp;
            if (resp.Status >= 400) throw new LoginRequiredException($"The login page returned HTTP {resp.Status} ({resp.Url.IdnHost}{resp.Url.AbsolutePath}).");

            var html = resp.Body;
            var forms = HtmlForms.Parse(html);
            web.Trace?.Invoke($"page {resp.Url.IdnHost}{resp.Url.AbsolutePath}: " + (forms.Count == 0 ? "no forms" :
                string.Join(" | ", forms.Select(f => $"{f.Method} {f.Action.Split('?')[0]} [{string.Join(",", f.Fields.Select(x => x.Name + ":" + x.Type))}]"))));

            // Never spin on the same page.
            var key = resp.Url.IdnHost + resp.Url.AbsolutePath;
            seen[key] = seen.GetValueOrDefault(key) + 1;
            if (seen[key] > 3) throw new LoginRequiredException("The login flow is stuck on " + key + ".");

            // 1. SAML hand-over: a browser submits this form with JavaScript; we submit it as is.
            if (forms.FirstOrDefault(f => f.Has("SAMLResponse") || f.Has("SAMLRequest")) is { } saml)
            { resp = await SubmitAsync(resp, saml, null, ct).ConfigureAwait(false); continue; }

            // 2. Identity-provider choice (Keycloak): take the university (Shibboleth) broker.
            if (o.LinkHints is { } hints && hints.Select(h => HtmlForms.FindLink(html, h)).FirstOrDefault(l => l is not null) is { } link)
            { resp = await web.GetAsync(new Uri(resp.Url, link), ct: ct).ConfigureAwait(false); continue; }

            // 3. The login form.
            if (forms.FirstOrDefault(f => f.HasPassword) is { } login)
            {
                if (o.Credentials is not { } c) throw new LoginRequiredException("Login required: no stored credentials.");
                if (submitted) throw new BadCredentialsException();   // the form is back after we sent the credentials
                var fields = login.Fields.ToList();
                var user = fields.FirstOrDefault(f => !f.IsButton && f.Type != "password" && UserFieldNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                           ?? fields.FirstOrDefault(f => f.Type is "text" or "email");
                var pass = fields.First(f => f.Type == "password");
                if (user is null) throw new LoginRequiredException("The login page has no user name field.");
                var body = login.Submission(Button(login)).Select(kv =>
                    kv.Key == user.Name ? KeyValuePair.Create(kv.Key, c.User) : kv.Key == pass.Name ? KeyValuePair.Create(kv.Key, c.Password) : kv).ToList();
                submitted = true;
                resp = await PostAsync(resp, login, body, ct).ConfigureAwait(false);
                continue;
            }

            // 3b. Auto-submit forms made only of hidden fields. The Shibboleth IdP's "client storage" page is one: a browser fills it
            //     with JavaScript (local storage support check) and posts it; without JavaScript the form is posted as is.
            if (forms.FirstOrDefault(f => f.Fields.Count > 0 && f.Fields.All(x => x.Type == "hidden")) is { } hidden)
            { resp = await SubmitAsync(resp, hidden, null, ct).ConfigureAwait(false); continue; }

            // 4. Attribute-release / terms page: just accept.
            if (forms.FirstOrDefault(f => Consent(f) is not null) is { } consent)
            { resp = await SubmitAsync(resp, consent, Consent(consent), ct).ConfigureAwait(false); continue; }

            // 5. Plain meta refresh.
            if (HtmlForms.MetaRefreshTarget(html) is { } target)
            { resp = await web.GetAsync(new Uri(resp.Url, target), ct: ct).ConfigureAwait(false); continue; }

            throw new LoginRequiredException($"Unexpected page during login ({key}); interactive login needed.");
        }
        throw new LoginRequiredException("The login flow did not finish.");
    }

    /// <summary>The button a person would press on the Shibboleth login form.</summary>
    static FormField? Button(HtmlForm f) =>
        f.Fields.FirstOrDefault(x => x.IsButton && x.Name.Equals("_eventId_proceed", StringComparison.OrdinalIgnoreCase))
        ?? f.Fields.FirstOrDefault(x => x.IsButton);

    static FormField? Consent(HtmlForm f) => f.HasPassword ? null :
        f.Fields.FirstOrDefault(x => x.IsButton && (x.Name.Equals("_eventId_proceed", StringComparison.OrdinalIgnoreCase)
            || x.Value.Contains("Confirm", StringComparison.OrdinalIgnoreCase) || x.Value.Contains("Akzeptieren", StringComparison.OrdinalIgnoreCase)));

    Task<WebResponse> SubmitAsync(WebResponse page, HtmlForm form, FormField? clicked, CancellationToken ct) =>
        PostAsync(page, form, form.Submission(clicked), ct);

    Task<WebResponse> PostAsync(WebResponse page, HtmlForm form, List<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        var target = form.Action.Length == 0 ? page.Url : new Uri(page.Url, form.Action);
        if (form.Method == "POST") return web.PostFormAsync(target, fields, ct: ct);
        var query = string.Join("&", fields.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        return web.GetAsync(new UriBuilder(target) { Query = query }.Uri, ct: ct);
    }
}
