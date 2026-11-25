using System.Globalization;
using System.Xml.Linq;

namespace UniDashboard.Core.Ufind;

/// <summary>One possible exam date of a course (u:find <c>group/exams/exam</c>); <c>ExtId</c> is stable across changes.</summary>
public record UfindExam(
    string ExtId, string Id, DateTimeOffset Begin, DateTimeOffset End, string TitleDe, string TitleEn, string? When,
    List<string> Examiners, DateTimeOffset? EnrollFrom, DateTimeOffset? EnrollTo, DateTimeOffset? DisenrollUntil)
{
    /// <summary>Date-only exams are published as begin == end (T00:00 – T00:00): the time is not known yet.</summary>
    public bool TimeKnown => End > Begin;
}

public record UfindGroup(
    string GroupId, string? RegisterId, string? MoodleUrl, int MaxParticipants, bool Livestream,
    DateTimeOffset? EnrollFrom, DateTimeOffset? EnrollTo, DateTimeOffset? DisenrollUntil,
    Dictionary<string, string> Comment, Dictionary<string, string> Performance, Dictionary<string, string> Literature,
    Dictionary<string, string> Examination, Dictionary<string, string> Preconditions,
    List<string> Lecturers, List<string> Languages, List<UfindExam>? Exams = null);

/// <summary>
/// One curriculum chapter a course is listed in. <c>Path</c> is the full id chain "a|b|c|d" (programme leader | … | curriculum | … | section);
/// the 3rd id is the curriculum (<c>vvz_sub.html?path=</c>), the last one the section/module.
/// </summary>
public record UfindChapter(string Path, string Category, string Subcategory, string Name);

/// <summary>Static course information from u:find. Serialised as JSON into the DB and diffed for changes.</summary>
public record UfindCourse(
    string LvNr, string Semester, string Version, string TitleDe, string TitleEn, string TypeCode, string TypeDesc,
    decimal Ects, decimal Sws, bool Immanent, string? OfferedBy, List<string> Curricula, List<UfindGroup> Groups, List<UfindChapter>? Chapters = null);

/// <summary>
/// u:find course catalogue (public, no login): GET m2-ufind.univie.ac.at/courses/{lvnr}/{semester} → XML.
/// Terms: non-commercial use only; users must be told the data is not provided by the university and may be wrong.
/// </summary>
public interface ICourseInfoSource
{
    Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default);
}

public class UfindClient(HttpClient http) : ICourseInfoSource
{
    public const string BaseUrl = "https://m2-ufind.univie.ac.at";

    /// <summary>HttpClient with gzip and a generic user agent (no personal details).</summary>
    public static HttpClient CreateHttpClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("UniDashboard/1.0 (personal, non-commercial)");
        return c;
    }
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _last = DateTime.MinValue;

    public async Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/courses/{Uri.EscapeDataString(lvNr)}/{Uri.EscapeDataString(semester)}";
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                var xml = await resp.Content.ReadAsStringAsync(ct);
                return ParseCourse(XDocument.Parse(xml));
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct); }
        }
    }

    /// <summary>Polite: at most one request every 400 ms across the process.</summary>
    static async Task ThrottleAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _last.AddMilliseconds(400) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
        }
        finally { Gate.Release(); }
    }

    // ---------- parsing (public for fixture tests; tolerant of missing/unknown elements)

    static readonly XNamespace Xml = XNamespace.Xml;

    public static UfindCourse? ParseCourse(XDocument doc)
    {
        var c = doc.Root;
        if (c is null || c.Name.LocalName != "course") return null;

        string Lang(string name, string lang) =>
            c.Elements(name).FirstOrDefault(e => (string?)e.Attribute(Xml + "lang") == lang)?.Value ?? "";

        var type = c.Element("type");
        var curricula = c.Element("chapters")?.Elements("chapter")
            .Select(ch => string.Join(" › ", new[]
            {
                ch.Elements("category").FirstOrDefault(e => (string?)e.Attribute(Xml + "lang") == "de")?.Value,
                ch.Elements("subcategory").FirstOrDefault(e => (string?)e.Attribute(Xml + "lang") == "de")?.Value,
            }.Where(s => !string.IsNullOrWhiteSpace(s)))).Where(s => s.Length > 0).Distinct().ToList() ?? [];

        var chapters = c.Element("chapters")?.Elements("chapter").Select(ch =>
        {
            string De(string n) => ch.Elements(n).FirstOrDefault(e => (string?)e.Attribute(Xml + "lang") == "de")?.Value.Trim() ?? "";
            return new UfindChapter((string?)ch.Attribute("path") ?? "", De("category"), De("subcategory"), De("name"));
        }).Where(x => x.Path.Length > 0).ToList() ?? [];

        var groups = new List<UfindGroup>();
        foreach (var g in c.Element("groups")?.Elements("group") ?? [])
        {
            var reg = g.Element("registrations");
            var enroll = reg?.Element("enroll");
            Dictionary<string, string> Info(string n) =>
                (g.Element("info")?.Elements(n) ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Value))
                    .ToDictionary(e => (string?)e.Attribute(Xml + "lang") ?? "de", e => e.Value.Trim(), StringComparer.Ordinal) is { } d ? d : [];

            groups.Add(new(
                (string?)g.Attribute("id") ?? "", (string?)g.Attribute("register"),
                g.Elements("platform").FirstOrDefault(p => (string?)p.Attribute("type") == "Moodle")?.Value.Trim(),
                int.TryParse(g.Element("maxparticipants")?.Value, out var mp) ? mp : 0,
                g.Element("livestream")?.Value == "true",
                Dt((string?)enroll?.Attribute("from")), Dt((string?)enroll?.Attribute("to")),
                Dt((string?)reg?.Element("disenroll")?.Attribute("until")),
                Info("comment"), Info("performance"), Info("literature"), Info("examination"), Info("preconditions"),
                (g.Element("lecturers")?.Elements("lecturer") ?? [])
                    .Select(l => $"{l.Element("firstname")?.Value} {l.Element("lastname")?.Value}".Trim()).Where(s => s.Length > 0).ToList(),
                (g.Element("languages")?.Elements("language") ?? []).Select(l => l.Value.Trim()).Where(s => s.Length > 0).ToList(),
                ParseExams(g)));
        }

        return new((string?)c.Attribute("id") ?? "", (string?)c.Attribute("when") ?? "", (string?)c.Attribute("version") ?? "",
            Lang("longname", "de"), Lang("longname", "en"), type?.Value ?? "", (string?)type?.Attribute("desc") ?? "",
            Dec(c.Element("ects")?.Value), Dec(c.Element("sws")?.Value), c.Element("immanent")?.Value == "true",
            c.Element("offeredby")?.Value.Trim(), curricula, groups, chapters);
    }

    static List<UfindExam> ParseExams(XElement group)
    {
        var list = new List<UfindExam>();
        foreach (var e in group.Element("exams")?.Elements("exam") ?? [])
        {
            var begin = Dt((string?)e.Attribute("begin"));
            if (begin is null) continue;
            var end = Dt((string?)e.Attribute("end")) ?? begin.Value;
            var id = (string?)e.Attribute("id") ?? "";
            var ext = (string?)e.Attribute("extid") is { Length: > 0 } x ? x : id;
            if (ext.Length == 0) continue;
            var reg = e.Element("registrations");     // may be empty: window not published yet
            string Title(string lang) => e.Elements("title").FirstOrDefault(t => (string?)t.Attribute(Xml + "lang") == lang)?.Value.Trim() ?? "";
            list.Add(new(ext, id, begin.Value, end, Title("de"), Title("en"),
                e.Elements("when").FirstOrDefault(w => (string?)w.Attribute(Xml + "lang") == "de")?.Value.Trim() is { Length: > 0 } wh ? wh : null,
                (e.Element("examiners")?.Elements("examiner") ?? []).Select(x => (string?)x.Attribute("id") ?? "").Where(s => s.Length > 0).ToList(),
                Dt((string?)reg?.Element("enroll")?.Attribute("from")), Dt((string?)reg?.Element("enroll")?.Attribute("to")),
                Dt((string?)reg?.Element("disenroll")?.Attribute("until"))));
        }
        return list.OrderBy(x => x.Begin).ToList();
    }

    static DateTimeOffset? Dt(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static decimal Dec(string? s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
