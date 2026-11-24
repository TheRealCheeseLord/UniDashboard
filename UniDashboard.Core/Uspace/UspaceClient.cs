using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using UniDashboard.Core.Auth;

namespace UniDashboard.Core.Uspace;

public record LecturerDto(string FirstName, string LastName, string Role);
public record SessionDto(DateTimeOffset Begin, DateTimeOffset End, string? Room);

/// <summary>One enrolment ("Anmeldung") of the student in a course group, from u:space.</summary>
public record EnrolmentDto(
    string LvNr, string GroupNr, string Semester, string Title, string Type, string Status, bool Immanent,
    List<LecturerDto> Lecturers, List<SessionDto> Sessions, string? MoodleUrl, long? MoodleCourseId,
    string? StudyName, int? WaitlistPosition, string? UfindLink, string? Remark)
{
    public string Key => $"{LvNr}-{GroupNr}-{Semester}";
}

/// <summary>
/// u:space (Liferay DXP behind Keycloak) — the enrolment overview ("Anmeldeübersicht").
/// The page itself calls a gateway: POST /o/weserve-routing-rest/v1.0/generic with a Liferay CSRF token header
/// (<c>authToken</c> in the page HTML) and a JSON envelope naming the target service/path.
/// Auth: saved DPAPI-encrypted session cookies (no browser) → headless Shibboleth login (same u:account as Moodle).
/// </summary>
/// <summary>An exam registration ("Prüfungsanmeldung") of the student. Parsed defensively: the exact JSON shape is unverified.</summary>
public record ExamRegistrationDto(string LvNr, string Semester, string ExtId, DateTimeOffset? Begin, DateTimeOffset? End, string? Room, string Status, string RawJson);

public interface IEnrolmentSource
{
    /// <summary>Exam registrations seen in the last <see cref="GetEnrolmentsAsync"/> call.</summary>
    IReadOnlyList<ExamRegistrationDto> LastExamRegistrations { get; }
    Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default);
}

public class UspaceClient(AppSettings settings, string? sessionPath = null) : IEnrolmentSource
{
    const string Base = "https://uspace.univie.ac.at";
    const string Page = Base + "/web/studium/anmeldeuebersicht";
    const string Target = "as-studierende-anmeldeuebersicht";

    readonly string _sessionPath = sessionPath ?? Path.Combine(Paths.Root, "uspace-session.bin");
    public string? LastMethod { get; private set; }
    public IReadOnlyList<ExamRegistrationDto> LastExamRegistrations { get; private set; } = [];

    public void ForgetSession() { try { File.Delete(_sessionPath); } catch (IOException) { } }

    /// <param name="headless">Agent: true — never show a browser. App: false — may show Chrome as a last resort.</param>
    public async Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default)
    {
        using var pw = await Playwright.CreateAsync();

        if (await TryFetchAsync(pw, ct) is { } fast) { LastMethod = "saved session"; return fast; }

        await using var gate = await AuthService.AcquireLockAsync(ct);
        // Another process may have just logged in while we waited for the lock.
        if (await TryFetchAsync(pw, ct) is { } again) { LastMethod = "saved session"; return again; }

        await LoginAsync(pw, headless, ct);
        LastMethod = "login";
        return await TryFetchAsync(pw, ct) ?? throw new LoginRequiredException("u:space session was not accepted after login");
    }

    // ---------- API with saved session (no browser)

    async Task<List<EnrolmentDto>?> TryFetchAsync(IPlaywright pw, CancellationToken ct)
    {
        var state = LoadSession();
        if (state is null) return null;
        var api = await pw.APIRequest.NewContextAsync(new() { StorageState = state, Timeout = 30_000 });
        try
        {
            var resp = await api.GetAsync(Page, new() { MaxRedirects = 0 });
            if (resp.Status != 200) { DeleteSession(); return null; }      // 302 → login: session expired
            var m = Regex.Match(await resp.TextAsync(), @"authToken['""]?\s*[:=]\s*['""]([^'""]+)['""]");
            if (!m.Success) { DeleteSession(); return null; }
            var csrf = m.Groups[1].Value;

            using var sems = await CallAsync(api, csrf, "GET", "/v2/anmeldung/semesters", null);
            if (sems is null) { DeleteSession(); return null; }
            var semesters = sems.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
            if (semesters.Count == 0) return [];

            using var list = await CallAsync(api, csrf, "POST", "/v2/anmeldung/semesters/anmeldungen", JsonSerializer.Serialize(semesters));
            if (list is null) { DeleteSession(); return null; }

            SaveSession(await api.StorageStateAsync());   // keep rolling cookies fresh
            LastExamRegistrations = ParseExamRegistrations(list);
            return ParseEnrolments(list);
        }
        catch (PlaywrightException) { return null; }
        finally { await api.DisposeAsync(); }
    }

    static async Task<JsonDocument?> CallAsync(IAPIRequestContext api, string csrf, string method, string path, string? body)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            request = new { method, targetService = Target, path, queryParams = "", body },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var resp = await api.PostAsync($"{Base}/o/weserve-routing-rest/v1.0/generic", new()
        {
            DataString = envelope,
            Headers = new Dictionary<string, string> { ["x-csrf-token"] = csrf, ["content-type"] = "application/json" },
            MaxRedirects = 0,
        });
        if (resp.Status is 401 or 403 or 302) return null;
        if (!resp.Ok) throw new HttpRequestException($"u:space API {path} returned HTTP {resp.Status}");
        return JsonDocument.Parse(await resp.TextAsync());
    }

    // ---------- login (Keycloak → Shibboleth broker → univie Weblogin)

    async Task LoginAsync(IPlaywright pw, bool headless, CancellationToken ct)
    {
        var creds = CredentialStore.Load();
        var blocked = creds is { } c0 && settings.RejectedCredentialHash == AuthService.CredHash(c0);
        if (headless && (creds is null || blocked))
            throw new LoginRequiredException(creds is null ? "No stored credentials for headless login" : "Stored credentials were rejected earlier — update them in Settings.");

        try { await BrowserLoginAsync(pw, creds, headlessMode: true, ct); return; }
        catch (BadCredentialsException)
        {
            if (creds is { } c) settings.RejectedCredentialHash = AuthService.CredHash(c);
            throw new LoginRequiredException("Stored univie credentials were rejected — update them in Settings.");
        }
        catch (LoginRequiredException) when (!headless) { }
        catch (PlaywrightException) when (!headless) { }

        if (headless) throw new LoginRequiredException("Headless u:space login did not complete");
        await BrowserLoginAsync(pw, creds, headlessMode: false, ct);
    }

    async Task BrowserLoginAsync(IPlaywright pw, (string User, string Password)? creds, bool headlessMode, CancellationToken ct)
    {
        await using var browser = await AuthService.LaunchAsync(pw, headlessMode);
        var context = await browser.NewContextAsync(new() { StorageState = LoadSession(), Locale = "de-AT" });
        context.SetDefaultTimeout(headlessMode ? 25_000 : 300_000);
        var page = await context.NewPageAsync();
        if (headlessMode)
            await page.RouteAsync("**/*", r =>
                r.Request.ResourceType is "image" or "font" or "stylesheet" or "media" ? r.AbortAsync() : r.ContinueAsync());

        var done = new Regex(@"^https://uspace\.univie\.ac\.at/(?!c/portal/login)");
        await page.GotoAsync(Page, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        if (!done.IsMatch(page.Url))
        {
            // Keycloak: choose the "Shibboleth" broker (= the univie Weblogin used for Moodle).
            var shib = page.Locator("a[href*='broker/shibboleth/login']").First;
            try { await shib.WaitForAsync(new() { Timeout = 8_000 }); await shib.ClickAsync(); }
            catch (TimeoutException) { /* already past it (IdP session alive) */ }

            if (!done.IsMatch(page.Url) && creds is { } c)
                await AuthService.SubmitCredentialsAsync(page, c, done);

            try { await page.WaitForURLAsync(done, new() { Timeout = headlessMode ? 25_000 : 300_000, WaitUntil = WaitUntilState.Commit }); }
            catch (TimeoutException) { throw new LoginRequiredException(headlessMode ? "Headless login did not complete (MFA required?)" : "Login timed out"); }
        }
        SaveSession(await context.StorageStateAsync());
    }

    // ---------- parsing (public for fixture tests)

    public static List<EnrolmentDto> ParseEnrolments(JsonDocument doc)
    {
        var list = new List<EnrolmentDto>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Exams ("pruefung") are handled separately later; only courses here.
            if (Str(item, "anmeldungTyp") is { Length: > 0 } t && t != "LEHRVERANSTALTUNG") continue;
            if (!item.TryGetProperty("lehrveranstaltung", out var lv) || lv.ValueKind != JsonValueKind.Object) continue;

            var lecturers = new List<LecturerDto>();
            if (lv.TryGetProperty("lehrende", out var le) && le.ValueKind == JsonValueKind.Array)
                foreach (var l in le.EnumerateArray()) lecturers.Add(new(Str(l, "vorname"), Str(l, "nachname"), Str(l, "rolle")));

            var sessions = new List<SessionDto>();
            if (lv.TryGetProperty("termine", out var te) && te.ValueKind == JsonValueKind.Array)
                foreach (var s in te.EnumerateArray())
                    if (s.TryGetProperty("beginn", out var b) && b.ValueKind == JsonValueKind.Number)
                    {
                        var begin = DateTimeOffset.FromUnixTimeMilliseconds(b.GetInt64());
                        var end = s.TryGetProperty("ende", out var e) && e.ValueKind == JsonValueKind.Number
                            ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64()) : begin.AddHours(1);
                        var room = Str(s, "raumName");
                        sessions.Add(new(begin, end, string.IsNullOrWhiteSpace(room) ? null : Regex.Replace(room, @"\s{2,}", " ").Trim()));
                    }

            string? moodleUrl = null; long? moodleId = null;
            if (item.TryGetProperty("elearningCourse", out var el) && el.ValueKind == JsonValueKind.Object && Str(el, "url") is { Length: > 0 } url)
            {
                moodleUrl = url;
                var m = Regex.Match(url, @"[?&]id=(\d+)");
                if (m.Success) moodleId = long.Parse(m.Groups[1].Value);
            }

            string? study = item.TryGetProperty("studium", out var st) && st.ValueKind == JsonValueKind.Object ? Str(st, "name") : null;
            int? wait = item.TryGetProperty("wartelistenPlatz", out var wp) && wp.ValueKind == JsonValueKind.Number ? wp.GetInt32() : null;

            list.Add(new(Str(lv, "lvNr"), Str(lv, "gruppenNr"), Str(lv, "semesterKuerzel") is { Length: > 0 } sem ? sem : Str(item, "semesterKuerzel"),
                Str(lv, "titel"), Str(lv, "typ"), Str(item, "status"),
                lv.TryGetProperty("pruefungsimmanent", out var im) && im.ValueKind == JsonValueKind.True,
                lecturers, sessions.OrderBy(x => x.Begin).ToList(), moodleUrl, moodleId, study, wait,
                Str(lv, "ufindLink") is { Length: > 0 } ul ? ul : null,
                Str(lv, "terminAnmerkungen") is { Length: > 0 } ta ? ta : null));
        }
        return list;
    }

    // ---------- exam registrations (defensive; shape verified only once the user has registered for an exam)

    static JsonElement? Pick(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            foreach (var p in obj.EnumerateObject())
                if (p.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return p.Value;
        return null;
    }

    static string? AsString(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.ToString(),
        _ => null,
    };

    static DateTimeOffset? AsDate(JsonElement? e)
    {
        if (e is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return d;
        return null;
    }

    public static List<ExamRegistrationDto> ParseExamRegistrations(JsonDocument doc)
    {
        var list = new List<ExamRegistrationDto>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var typ = Str(item, "anmeldungTyp");
            var hasPruefung = item.TryGetProperty("pruefung", out var p) && p.ValueKind == JsonValueKind.Object;
            if (!hasPruefung && (typ.Length == 0 || typ == "LEHRVERANSTALTUNG")) continue;

            var exam = hasPruefung ? p : item;
            JsonElement? lv = item.TryGetProperty("lehrveranstaltung", out var l) && l.ValueKind == JsonValueKind.Object ? l
                : Pick(exam, "lehrveranstaltung");
            var lvNr = AsString(lv is { } x ? Pick(x, "lvNr") : null) ?? AsString(Pick(exam, "lvNr", "lvnr")) ?? "";
            var ext = AsString(Pick(exam, "extId", "externeId", "pruefungExtId", "pruefungsExtId", "pruefungId", "pruefungsId", "id"))
                      ?? AsString(Pick(item, "extId", "pruefungId", "id")) ?? "";
            var begin = AsDate(Pick(exam, "beginn", "beginnZeit", "beginnDatum", "start", "datum", "termin", "pruefungsdatum", "datumVon", "von"));
            var end = AsDate(Pick(exam, "ende", "endeZeit", "endDatum", "bis", "datumBis"));
            var room = AsString(Pick(exam, "raumName", "raum", "ort", "pruefungsort"));
            var sem = Str(item, "semesterKuerzel");
            if (sem.Length == 0 && lv is { } lvv) sem = Str(lvv, "semesterKuerzel");
            list.Add(new(lvNr, sem, ext, begin, end, room, Str(item, "status"), item.GetRawText()));
        }
        return list;
    }

    /// <summary>Keys and value types only (no values), for diagnosing an unknown JSON shape.</summary>
    public static string ShapeOf(JsonElement e, int depth = 0)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object when depth < 4:
                return "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + ShapeOf(p.Value, depth + 1))) + "}";
            case JsonValueKind.Array:
                return "[" + (e.GetArrayLength() > 0 ? ShapeOf(e[0], depth + 1) : "") + "]";
            case JsonValueKind.Object: return "{…}";
            default: return e.ValueKind.ToString().ToLowerInvariant();
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---------- saved session (DPAPI, current user)

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
}
