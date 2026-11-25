using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace UniDashboard.Core.Ufind;

/// <summary>An exam attached to a curriculum section (module / STEOP / study section), e.g. "Modulprüfung" of STEOP I.</summary>
public record CurriculumExamRef(string ModPath, string Eq, string Title, List<string> Labels);

/// <summary>A course (LV) listed under a curriculum section.</summary>
public record CurriculumCourse(string LvNr, string Type, string Title);

/// <summary>One node of the ufind study plan tree (<c>vvz_sub.html?path=…</c>).</summary>
public class CurriculumNode
{
    /// <summary>Parser generation of a stored tree; older trees (smaller number) are refetched so parser fixes reach stored data.</summary>
    public int Schema { get; set; }
    public string PathId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<CurriculumNode> Children { get; set; } = [];
    public List<CurriculumCourse> Courses { get; set; } = [];
    public List<CurriculumExamRef> Exams { get; set; } = [];

    /// <summary>This node and all descendants, depth-first.</summary>
    public IEnumerable<CurriculumNode> Flatten()
    {
        yield return this;
        foreach (var c in Children) foreach (var n in c.Flatten()) yield return n;
    }
}

/// <summary>Exam dates of one section exam (<c>exam.html?mod=…&amp;eq=…</c>).</summary>
public record CurriculumExam(string ModPath, string Eq, string Title, string ExamType, decimal Ects, List<UfindExam> Dates);

public interface ICurriculumSource
{
    Task<CurriculumNode?> GetTreeAsync(string pathId, string semester, CancellationToken ct = default);
    Task<CurriculumExam?> GetExamAsync(string modPath, string eq, string semester, CancellationToken ct = default);
}

/// <summary>
/// u:find study plan (public, server-rendered HTML, no login): the hierarchy of a curriculum with its section exams.
/// Scraped with tolerant regexes: unknown markup yields fewer items, never an exception.
/// </summary>
public class CurriculumClient(HttpClient http) : ICurriculumSource
{
    public const string BaseUrl = "https://ufind.univie.ac.at/de";
    /// <summary>Bump when the parser output changes in a way stored trees should be refreshed for.</summary>
    public const int CurrentSchema = 2;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _last = DateTime.MinValue;

    public async Task<CurriculumNode?> GetTreeAsync(string pathId, string semester, CancellationToken ct = default)
    {
        var html = await GetAsync($"{BaseUrl}/vvz_sub.html?semester={Uri.EscapeDataString(semester)}&path={Uri.EscapeDataString(pathId)}", ct);
        return html is null ? null : ParseSection(html, pathId);
    }

    public async Task<CurriculumExam?> GetExamAsync(string modPath, string eq, string semester, CancellationToken ct = default)
    {
        var html = await GetAsync($"{BaseUrl}/exam.html?mod={Uri.EscapeDataString(modPath)}&eq={Uri.EscapeDataString(eq)}&semester={Uri.EscapeDataString(semester)}", ct);
        return html is null ? null : ParseExam(html, modPath, eq);
    }

    async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct); }
        }
    }

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

    // ---------- parsing (public for fixture tests)

    static readonly Regex Token = new(
        @"<h(?<h>[2-6])\s+class=""list level\d level"">\s*<a[^>]*href=""[^""]*?path=(?<path>[^&""]+)[^""]*""[^>]*>(?<title>.*?)</a>"
        + @"|<li\s+class=""list exam[^""]*"">(?<exam>.*?)</li>"
        + @"|<li\s+class=""list course[^""]*"">(?<course>.*?)</li>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ExamHref = new(@"href=""exam\.html\?[^""]*?mod=(?<mod>[^&""]+)&amp;eq=(?<eq>[^&""]+)[^""]*""[^>]*>(?<t>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ExamHrefAlt = new(@"href=""exam\.html\?[^""]*?eq=(?<eq>[^&""]+)[^""]*?&amp;mod=(?<mod>[^&""]+)[^""]*""[^>]*>(?<t>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LabelText = new(@"class=""content""[^>]*>(?<l>[^<]*)<", RegexOptions.Compiled);
    static readonly Regex CourseRx = new(
        @"class=""number"">(?<nr>[^<]*)</span>\s*<abbr[^>]*>(?<type>[^<]*)</abbr>.*?<a[^>]*href=""course\.html\?lv=(?<lv>[^&""]+)[^""]*""[^>]*>(?<t>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a <c>vvz_sub.html</c> page into a tree rooted at <paramref name="rootPath"/>. Section headings are
    /// h2…h6 (h2 = direct child of the root); exams and courses belong to the most recent heading.
    /// </summary>
    public static CurriculumNode ParseSection(string html, string rootPath)
    {
        var root = new CurriculumNode { PathId = rootPath, Title = PageTitle(html), Schema = CurrentSchema };
        var stack = new List<(int Level, CurriculumNode Node)> { (1, root) };
        foreach (Match m in Token.Matches(html))
        {
            if (m.Groups["h"].Success)
            {
                var level = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                var node = new CurriculumNode { PathId = m.Groups["path"].Value, Title = Text(m.Groups["title"].Value) };
                while (stack.Count > 1 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
                stack[^1].Node.Children.Add(node);
                stack.Add((level, node));
            }
            else if (m.Groups["exam"].Success)
            {
                var body = m.Groups["exam"].Value;
                var h = ExamHref.Match(body);
                if (!h.Success) h = ExamHrefAlt.Match(body);
                if (!h.Success) continue;
                var labels = LabelText.Matches(body).Select(x => Text(x.Groups["l"].Value)).Where(s => s.Length > 0 && s != "PRÜFUNG" && s != "EXAM").ToList();
                stack[^1].Node.Exams.Add(new(h.Groups["mod"].Value, h.Groups["eq"].Value, Text(h.Groups["t"].Value), labels));
            }
            else
            {
                var c = CourseRx.Match(m.Groups["course"].Value);
                if (c.Success) stack[^1].Node.Courses.Add(new(c.Groups["lv"].Value.Trim(), Text(c.Groups["type"].Value), Text(c.Groups["t"].Value)));
            }
        }
        return root;
    }

    /// <summary>
    /// The page's own name is its h1 (e.g. "Diplom Katholische Fachtheologie (011 [5] - Version 2026)"). The breadcrumb only lists the
    /// parents (the last entry is the programme leadership, "Studienprogrammleitung 1 - …"), so it is just a fallback.
    /// </summary>
    static string PageTitle(string html)
    {
        // Skip the hidden "Lokale Navigation:" h1 and the generic "Kapitel im Vorlesungsverzeichnis" heading.
        foreach (Match h1 in Regex.Matches(html, @"<h1(?<attrs>[^>]*)>(?<t>.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (h1.Groups["attrs"].Value.Contains("hidden", StringComparison.OrdinalIgnoreCase)) continue;
            var t = Text(h1.Groups["t"].Value);
            if (t.Length > 0 && !t.StartsWith("Kapitel im", StringComparison.OrdinalIgnoreCase)) return t;
        }
        return BreadcrumbLeaf(html);
    }

    static string BreadcrumbLeaf(string html)
    {
        var m = Regex.Matches(html, @"<div class=""level\d""><a[^>]*>(?<t>.*?)</a></div>", RegexOptions.Singleline);
        return m.Count > 0 ? Text(m[^1].Groups["t"].Value) : "";
    }

    static readonly Regex DateLink = new(
        @"<a\s+href=""exam\.html\?prueid=(?<id>[^&""]+)[^""]*""[^>]*>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses an <c>exam.html</c> page into the exam header and its dates (date-only dates get begin == end).</summary>
    public static CurriculumExam ParseExam(string html, string modPath, string eq)
    {
        string Span(string cls, string src) =>
            Text(Regex.Match(src, $@"class=""{cls}""[^>]*>(?<v>.*?)</span>", RegexOptions.Singleline).Groups["v"].Value);
        var title = Text(Regex.Match(html, @"<span class=""what"">(?<v>.*?)</span>", RegexOptions.Singleline).Groups["v"].Value);
        var type = Text(Regex.Match(html, @"class=""examtype""><em>(?<v>.*?)</em>", RegexOptions.Singleline).Groups["v"].Value);
        var ects = decimal.TryParse(Span("ects", html), NumberStyles.Any, CultureInfo.InvariantCulture, out var e) ? e : 0;

        var dates = new List<UfindExam>();
        foreach (Match m in DateLink.Matches(html))
        {
            var body = m.Groups["body"].Value;
            if (!DateTime.TryParseExact(Span("date", body), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            var begin = day; var end = day;
            var t = Regex.Match(Span("time", body), @"(?<a>\d{1,2}:\d{2})\s*-\s*(?<b>\d{1,2}:\d{2})");
            if (t.Success)
            {
                begin = day + TimeSpan.Parse(t.Groups["a"].Value, CultureInfo.InvariantCulture);
                end = day + TimeSpan.Parse(t.Groups["b"].Value, CultureInfo.InvariantCulture);
            }
            var room = Regex.Replace(Text(Regex.Match(body, @"class=""room""[^>]*>(?<v>.*?)</span>", RegexOptions.Singleline).Groups["v"].Value), @"\s{2,}", " ");
            var id = m.Groups["id"].Value;
            dates.Add(new($"sect:{id}", id, Vienna(begin), Vienna(end), title, title, room.Length > 0 ? room : null, [], null, null, null));
        }
        return new(modPath, eq, title, type, ects, dates.OrderBy(d => d.Begin).ToList());
    }

    static DateTimeOffset Vienna(DateTime local)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    static string Text(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]*>", "")).Trim();
}
