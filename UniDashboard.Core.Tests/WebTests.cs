using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UniDashboard.Core;
using UniDashboard.Core.Auth;
using UniDashboard.Core.Web;

namespace UniDashboard.Core.Tests;

/// <summary>Scripted fake server: records every request (method, url, cookie header, body) and answers via the callback.</summary>
sealed class FakeWeb(Func<FakeWeb.Req, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Req(HttpMethod Method, Uri Url, string Cookie, string Body);
    public readonly List<Req> Log = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        request.Headers.TryGetValues("Cookie", out var c);
        var r = new Req(request.Method, request.RequestUri!, c is null ? "" : string.Join("; ", c), body);
        Log.Add(r);
        return respond(r);
    }

    public static HttpResponseMessage Html(string html, params string[] setCookies) => With(HttpStatusCode.OK, new StringContent(html, Encoding.UTF8, "text/html"), setCookies);
    public static HttpResponseMessage Redirect(string location, params string[] setCookies) => With(HttpStatusCode.Found, new StringContent(""), setCookies, location);

    static HttpResponseMessage With(HttpStatusCode code, HttpContent content, string[] setCookies, string? location = null)
    {
        var r = new HttpResponseMessage(code) { Content = content };
        if (location is not null) r.Headers.TryAddWithoutValidation("Location", location);
        foreach (var sc in setCookies) r.Headers.TryAddWithoutValidation("Set-Cookie", sc);
        return r;
    }
}

public class HtmlFormsTests
{
    const string LoginPage = """
        <html><head><script>var x = '<form action="/evil"><input name="evil"></form>';</script></head><body>
        <!-- <form action="/commented"><input name="ghost"></form> -->
        <form action="/idp/profile/SAML2/Redirect/SSO?execution=e1s1" method="post">
          <input type="hidden" name="csrf_token" value="a&amp;b">
          <input type='text' name="j_username" value="">
          <input type=password name=j_password>
          <input type="checkbox" name="remember" value="1">
          <input type="checkbox" name="keep" value="1" checked>
          <button type="submit" name="_eventId_proceed" value="Login">Anmelden</button>
        </form></body></html>
        """;

    [Fact]
    public void Parses_forms_fields_and_ignores_scripts_and_comments()
    {
        var forms = HtmlForms.Parse(LoginPage);
        var f = Assert.Single(forms);
        Assert.Equal("/idp/profile/SAML2/Redirect/SSO?execution=e1s1", f.Action);
        Assert.Equal("POST", f.Method);
        Assert.True(f.HasPassword);
        Assert.Equal("a&b", f.Fields.First(x => x.Name == "csrf_token").Value);   // entities decoded
        Assert.DoesNotContain(f.Fields, x => x.Name == "remember");               // unchecked checkbox is not sent
        Assert.Contains(f.Fields, x => x.Name == "keep");
    }

    [Fact]
    public void Submission_has_non_button_fields_plus_the_clicked_button()
    {
        var f = HtmlForms.Parse(LoginPage)[0];
        var sub = f.Submission(f.Fields.First(x => x.Name == "_eventId_proceed"));
        Assert.Contains(sub, kv => kv.Key == "csrf_token");
        Assert.Contains(sub, kv => kv is { Key: "_eventId_proceed", Value: "Login" });
        Assert.DoesNotContain(HtmlForms.Parse(LoginPage)[0].Submission(), kv => kv.Key == "_eventId_proceed");
    }

    [Fact]
    public void Finds_links_classes_and_meta_refresh()
    {
        const string html = """
            <meta http-equiv="Refresh" content="0; URL='/next?a=1&amp;b=2'">
            <div class="box form-error"><a href="/auth/realms/x/broker/shibboleth/login?client_id=c&amp;tab=1">uni</a></div>
            """;
        Assert.Equal("/auth/realms/x/broker/shibboleth/login?client_id=c&tab=1", HtmlForms.FindLink(html, "broker/shibboleth/login"));
        Assert.Null(HtmlForms.FindLink(html, "nothing"));
        Assert.True(HtmlForms.HasAnyClass(html, "form-error", "alert-danger"));
        Assert.False(HtmlForms.HasAnyClass(html, "alert-danger"));
        Assert.Equal("/next?a=1&b=2", HtmlForms.MetaRefreshTarget(html));
    }
}

public class WebSessionTests
{
    static WebSession Session(FakeWeb h) => new(["example.test"], h);
    static Uri U(string s) => new(s);

    [Fact]
    public async Task Follows_redirects_manually_and_carries_cookies()
    {
        var h = new FakeWeb(r => r.Url.AbsolutePath switch
        {
            "/start" => FakeWeb.Redirect("https://idp.example.test/login", "sid=abc; Path=/; Secure; HttpOnly"),
            "/login" => FakeWeb.Html("login", "idp=1; Path=/; Secure"),
            _ => FakeWeb.Html("?"),
        });
        using var web = Session(h);
        var resp = await web.GetAsync(U("https://moodle.example.test/start"));
        Assert.Equal("https://idp.example.test/login", resp.Url.ToString());
        Assert.Equal("login", resp.Body);
        // cookies are per host: the idp request must not carry the moodle cookie
        Assert.Equal("", h.Log[1].Cookie);
        await web.GetAsync(U("https://moodle.example.test/again"));
        Assert.Contains("sid=abc", h.Log[2].Cookie);
    }

    [Fact]
    public async Task Refuses_non_https_and_foreign_hosts_including_redirect_targets()
    {
        var h = new FakeWeb(r => r.Url.AbsolutePath == "/to-http" ? FakeWeb.Redirect("http://moodle.example.test/x")
            : r.Url.AbsolutePath == "/to-evil" ? FakeWeb.Redirect("https://evil.test/x") : FakeWeb.Html("ok"));
        using var web = Session(h);
        await Assert.ThrowsAsync<WebSecurityException>(() => web.GetAsync(U("http://moodle.example.test/")));
        await Assert.ThrowsAsync<WebSecurityException>(() => web.GetAsync(U("https://evil.test/")));
        await Assert.ThrowsAsync<WebSecurityException>(() => web.GetAsync(U("https://moodle.example.test/to-http")));
        await Assert.ThrowsAsync<WebSecurityException>(() => web.GetAsync(U("https://moodle.example.test/to-evil")));
        Assert.DoesNotContain(h.Log, r => r.Url.Host == "evil.test");   // never even connected
        Assert.False(web.IsAllowed(U("https://notexample.test/")));      // suffix match is on a dot boundary
    }

    [Fact]
    public async Task Stops_redirect_loops()
    {
        var h = new FakeWeb(_ => FakeWeb.Redirect("https://moodle.example.test/loop"));
        using var web = Session(h);
        await Assert.ThrowsAsync<WebSecurityException>(() => web.GetAsync(U("https://moodle.example.test/loop")));
        Assert.Equal(WebSession.MaxRedirects + 1, h.Log.Count);
    }

    [Fact]
    public async Task Does_not_follow_when_asked_and_returns_custom_scheme_redirects()
    {
        var h = new FakeWeb(r => r.Url.AbsolutePath == "/launch" ? FakeWeb.Redirect("moodlemobile://token=QUJD") : FakeWeb.Html("x"));
        using var web = Session(h);
        var a = await web.GetAsync(U("https://moodle.example.test/launch"), follow: false);
        var b = await web.GetAsync(U("https://moodle.example.test/launch"), follow: true);   // non-http target: handed back, not followed
        Assert.Equal("moodlemobile://token=QUJD", a.Location);
        Assert.Equal("moodlemobile://token=QUJD", b.Location);
        Assert.Equal(2, h.Log.Count);
    }

    [Fact]
    public async Task Post_redirect_becomes_get()
    {
        var h = new FakeWeb(r => r.Method == HttpMethod.Post ? FakeWeb.Redirect("/done") : FakeWeb.Html("done"));
        using var web = Session(h);
        var resp = await web.PostFormAsync(U("https://moodle.example.test/post"), [KeyValuePair.Create("a", "b")]);
        Assert.Equal("done", resp.Body);
        Assert.Equal(HttpMethod.Get, h.Log[1].Method);
        Assert.Equal("", h.Log[1].Body);
    }

    [Fact]
    public async Task Export_import_round_trip_keeps_cookies()
    {
        var h = new FakeWeb(_ => FakeWeb.Html("x", "a=1; Path=/; Secure; Expires=Wed, 01 Jan 2099 00:00:00 GMT", "b=2; Path=/; Secure"));
        using var one = Session(h);
        await one.GetAsync(U("https://moodle.example.test/"));
        var json = one.Export();

        var h2 = new FakeWeb(_ => FakeWeb.Html("x"));
        using var two = Session(h2);
        two.Import(json);
        await two.GetAsync(U("https://moodle.example.test/"));
        Assert.Contains("a=1", h2.Log[0].Cookie);
        Assert.Contains("b=2", h2.Log[0].Cookie);
    }

    [Fact]
    public async Task Imports_session_files_written_by_the_old_playwright_version()
    {
        const string playwright = """
            {"cookies":[
              {"name":"MoodleSession","value":"m1","domain":"moodle.example.test","path":"/","expires":-1,"httpOnly":true,"secure":true,"sameSite":"Lax"},
              {"name":"idp","value":"i1","domain":".example.test","path":"/","expires":4102444800,"httpOnly":true,"secure":true,"sameSite":"None"},
              {"name":"old","value":"x","domain":"moodle.example.test","path":"/","expires":1000000000,"httpOnly":false,"secure":true,"sameSite":"Lax"}
            ],"origins":[{"origin":"https://moodle.example.test","localStorage":[]}]}
            """;
        var h = new FakeWeb(_ => FakeWeb.Html("x"));
        using var web = Session(h);
        web.Import(playwright);
        await web.GetAsync(U("https://moodle.example.test/"));
        Assert.Contains("MoodleSession=m1", h.Log[0].Cookie);
        Assert.Contains("idp=i1", h.Log[0].Cookie);
        Assert.DoesNotContain("old=", h.Log[0].Cookie);   // expired cookies are dropped

        web.Import("not json at all");   // corrupt input is ignored, never throws
        web.Import(null);
    }
}

public class UnivieLoginTests
{
    static readonly (string User, string Password)? Creds = ("a12345678", "s3cret&pw");
    const string Idp = "https://idp.example.test";
    const string LoginForm = """
        <form action="/idp/login?execution=e1s1" method="post">
          <input type="hidden" name="csrf_token" value="tok123">
          <input name="j_username"><input type="password" name="j_password">
          <button type="submit" name="_eventId_proceed" value="">Login</button>
        </form>
        """;
    const string SamlForm = """
        <form action="https://moodle.example.test/Shibboleth.sso/SAML2/POST" method="post">
          <input type="hidden" name="RelayState" value="rs"><input type="hidden" name="SAMLResponse" value="PHNhbWw+">
          <noscript><input type="submit" value="Continue"></noscript>
        </form>
        """;

    static UnivieLogin.Options Opts((string, string)? creds, params string[] hints) =>
        new(u => u.Host == "moodle.example.test" && !u.AbsolutePath.StartsWith("/login/"), creds, hints.Length == 0 ? null : hints);

    /// <summary>moodle /login -> idp login form -> (credentials) -> SAML hand-over form -> moodle ACS -> /my.</summary>
    static FakeWeb Server(Func<FakeWeb.Req, HttpResponseMessage?>? tweak = null, bool wrongPassword = false, bool consent = false, bool idpSessionAlive = false) => new(r =>
    {
        if (tweak?.Invoke(r) is { } custom) return custom;
        return (r.Url.Host, r.Url.AbsolutePath, r.Method.Method) switch
        {
            ("moodle.example.test", "/login/index.php", _) => FakeWeb.Redirect(idpSessionAlive ? "/my/" : Idp + "/idp/login"),
            ("moodle.example.test", "/my/", _) => FakeWeb.Html("dashboard"),
            ("idp.example.test", "/idp/login", "GET") => FakeWeb.Html(LoginForm),
            ("idp.example.test", "/idp/login", "POST") => wrongPassword ? FakeWeb.Html(LoginForm + "<p class='form-error'>bad</p>")
                : consent ? FakeWeb.Html("""<form action="/idp/consent" method="post"><input type="hidden" name="csrf_token" value="c"><button name="_eventId_proceed" value="Akzeptieren">ok</button></form>""")
                : FakeWeb.Html(SamlForm),
            ("idp.example.test", "/idp/consent", "POST") => FakeWeb.Html(SamlForm),
            ("moodle.example.test", "/Shibboleth.sso/SAML2/POST", "POST") => FakeWeb.Redirect("/my/", "MoodleSession=ok; Path=/; Secure"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    });

    static WebSession Session(FakeWeb h) => new(["example.test"], h);

    [Fact]
    public async Task Happy_path_fills_the_login_form_with_hidden_fields_and_submits_the_saml_handover()
    {
        var h = Server();
        using var web = Session(h);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds));
        Assert.Equal("dashboard", resp.Body);

        var post = h.Log.First(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath == "/idp/login");
        Assert.Equal("idp.example.test", post.Url.Host);
        Assert.Contains("j_username=a12345678", post.Body);
        Assert.Contains("j_password=s3cret%26pw", post.Body);   // form-encoded, not sent raw
        Assert.Contains("csrf_token=tok123", post.Body);          // hidden field carried over
        Assert.Contains("_eventId_proceed=", post.Body);          // the "clicked" button

        var saml = h.Log.First(r => r.Url.AbsolutePath == "/Shibboleth.sso/SAML2/POST");
        Assert.Contains("SAMLResponse=", saml.Body);
        Assert.Contains("RelayState=rs", saml.Body);
        Assert.DoesNotContain("s3cret", saml.Body);
        // the password only ever went to the IdP
        Assert.All(h.Log.Where(r => r.Body.Contains("s3cret")), r => Assert.Equal("idp.example.test", r.Url.Host));
    }

    [Fact]
    public async Task Skips_the_form_when_the_idp_session_is_alive()
    {
        var h = Server(idpSessionAlive: true);
        using var web = Session(h);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(null));
        Assert.Equal("dashboard", resp.Body);
        Assert.DoesNotContain(h.Log, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Wrong_password_is_detected_after_one_attempt()
    {
        var h = Server(wrongPassword: true);
        using var web = Session(h);
        await Assert.ThrowsAsync<BadCredentialsException>(() =>
            new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds)));
        Assert.Single(h.Log, r => r.Method == HttpMethod.Post);   // never retried: no lockout risk
    }

    [Fact]
    public async Task Login_form_without_credentials_needs_interactive_login()
    {
        var h = Server();
        using var web = Session(h);
        var ex = await Assert.ThrowsAsync<LoginRequiredException>(() =>
            new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(null)));
        Assert.Contains("credentials", ex.Message);
        Assert.DoesNotContain(h.Log, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Accepts_the_consent_page()
    {
        var h = Server(consent: true);
        using var web = Session(h);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds));
        Assert.Equal("dashboard", resp.Body);
        var consent = h.Log.First(r => r.Url.AbsolutePath == "/idp/consent");
        Assert.Contains("_eventId_proceed=Akzeptieren", consent.Body);
        Assert.DoesNotContain("j_password", consent.Body);
    }

    [Fact]
    public async Task Picks_the_shibboleth_broker_on_the_keycloak_page()
    {
        var h = Server(r => r switch
        {
            { Url.Host: "uspace.example.test", Url.AbsolutePath: "/web/anmeldung" } => FakeWeb.Redirect("https://kc.example.test/auth"),
            { Url.Host: "kc.example.test", Url.AbsolutePath: "/auth" } => FakeWeb.Html("""
                <form action="/auth/local" method="post"><input name="username"><input type="password" name="password"><button>Sign in</button></form>
                <a href="/auth/broker/shibboleth/login?client=c&amp;tab=t">University login</a>
                """),
            { Url.Host: "kc.example.test", Url.AbsolutePath: "/auth/broker/shibboleth/login" } => FakeWeb.Redirect(Idp + "/idp/login"),
            { Url.Host: "idp.example.test", Url.AbsolutePath: "/idp/login", Method.Method: "POST" } =>
                FakeWeb.Html(SamlForm.Replace("moodle.example.test", "uspace.example.test")),
            { Url.Host: "uspace.example.test", Url.AbsolutePath: "/Shibboleth.sso/SAML2/POST" } => FakeWeb.Redirect("/home"),
            { Url.Host: "uspace.example.test", Url.AbsolutePath: "/home" } => FakeWeb.Html("enrolments"),
            _ => null,
        });
        using var web = Session(h);
        var opts = new UnivieLogin.Options(u => u.Host == "uspace.example.test" && u.AbsolutePath == "/home", Creds, ["broker/shibboleth/login"]);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://uspace.example.test/web/anmeldung"), opts);
        Assert.Equal("enrolments", resp.Body);
        // the Keycloak-local password form was NOT used, the broker link was
        Assert.DoesNotContain(h.Log, r => r.Url.AbsolutePath == "/auth/local");
        Assert.Contains(h.Log, r => r.Url.AbsolutePath == "/auth/broker/shibboleth/login");
    }

    [Fact]
    public async Task Posts_the_idp_client_storage_page_as_is_like_a_browser_without_javascript()
    {
        // Real behaviour of the univie IdP: before the login form it serves a form of only hidden shib_idp_ls_* fields.
        var h = Server(r => r switch
        {
            { Url.AbsolutePath: "/idp/login", Method.Method: "GET" } when !r.Cookie.Contains("ls=done") => FakeWeb.Html("""
                <form action="/idp/ls" method="post">
                  <input type="hidden" name="shib_idp_ls_exception.shib_idp_session_ss" value="">
                  <input type="hidden" name="shib_idp_ls_supported" value="">
                  <input type="hidden" name="_eventId_proceed" value="">
                </form>
                """),
            { Url.AbsolutePath: "/idp/ls" } => FakeWeb.Redirect("/idp/login", "ls=done; Path=/; Secure"),
            _ => null,
        });
        using var web = Session(h);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds));
        Assert.Equal("dashboard", resp.Body);
        var ls = h.Log.First(r => r.Url.AbsolutePath == "/idp/ls");
        Assert.Contains("shib_idp_ls_supported=", ls.Body);
        Assert.DoesNotContain("s3cret", ls.Body);   // the storage form never carries credentials
    }

    [Fact]
    public async Task Saml_handover_wins_over_a_broker_link_on_the_same_page()
    {
        // Real behaviour of Keycloak: the page reached via the broker link is itself a SAML auto-post page that also mentions the link.
        var h = Server(r => r switch
        {
            { Url.Host: "uspace.example.test", Url.AbsolutePath: "/web/anmeldung" } => FakeWeb.Redirect("https://kc.example.test/broker/shibboleth/login"),
            { Url.Host: "kc.example.test", Url.AbsolutePath: "/broker/shibboleth/login" } => FakeWeb.Html(
                """<a href="/broker/shibboleth/login?restart=1">restart</a><form action="https://idp.example.test/saml/sso" method="post"><input type="hidden" name="SAMLRequest" value="x"></form>"""),
            { Url.Host: "idp.example.test", Url.AbsolutePath: "/saml/sso" } => FakeWeb.Redirect("https://uspace.example.test/home"),
            { Url.Host: "uspace.example.test", Url.AbsolutePath: "/home" } => FakeWeb.Html("enrolments"),
            _ => null,
        });
        using var web = Session(h);
        var opts = new UnivieLogin.Options(u => u.Host == "uspace.example.test" && u.AbsolutePath == "/home", Creds, ["broker/shibboleth/login"]);
        var resp = await new UnivieLogin(web).RunAsync(new Uri("https://uspace.example.test/web/anmeldung"), opts);
        Assert.Equal("enrolments", resp.Body);
        Assert.DoesNotContain(h.Log, r => r.Url.Query.Contains("restart"));
    }

    [Fact]
    public async Task Unknown_page_asks_for_interactive_login_instead_of_guessing()
    {
        var h = Server(r => r.Url.AbsolutePath == "/idp/login" ? FakeWeb.Html("<h1>Please confirm with your authenticator app</h1>") : null);
        using var web = Session(h);
        var ex = await Assert.ThrowsAsync<LoginRequiredException>(() =>
            new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds)));
        Assert.Contains("interactive", ex.Message);
    }

    [Fact]
    public async Task Never_sends_credentials_to_a_foreign_host_even_if_the_page_asks()
    {
        var h = Server(r => r.Url.AbsolutePath == "/idp/login" && r.Method == HttpMethod.Get
            ? FakeWeb.Html("""<form action="https://evil.test/steal" method="post"><input name="j_username"><input type="password" name="j_password"></form>""") : null);
        using var web = Session(h);
        await Assert.ThrowsAsync<WebSecurityException>(() =>
            new UnivieLogin(web).RunAsync(new Uri("https://moodle.example.test/login/index.php"), Opts(Creds)));
        Assert.DoesNotContain(h.Log, r => r.Url.Host == "evil.test");
    }
}
