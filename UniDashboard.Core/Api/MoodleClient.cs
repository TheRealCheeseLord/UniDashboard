using System.Text.Json;
using System.Text.RegularExpressions;
using UniDashboard.Core.Auth;

namespace UniDashboard.Core.Api;

public record CourseDto(long Id, string FullName, string ShortName);
public record AssignmentDto(long Id, long CourseId, long Cmid, string Name, string Intro, long DueUnix, List<AttachmentInfo> Attachments);
public record SubmissionStatus(bool Submitted, string? Grade, string? Feedback);

/// <summary>REST client for the Moodle web service. Re-authenticates once on invalidtoken.</summary>
public class MoodleClient(HttpClient http, AppSettings settings, AuthService auth, bool headlessReauth)
{
    string? _token;

    public string AssignmentUrl(long cmid) => $"{settings.MoodleBaseUrl}/mod/assign/view.php?id={cmid}";

    async Task<string> TokenAsync(CancellationToken ct)
    {
        _token ??= auth.CurrentToken();
        _token ??= await auth.LoginAsync(headlessReauth, ct);
        return _token;
    }

    public async Task<JsonDocument> CallAsync(string function, IEnumerable<KeyValuePair<string, string>>? args = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("wstoken", await TokenAsync(ct)), new("moodlewsrestformat", "json"), new("wsfunction", function),
            };
            if (args != null) form.AddRange(args);
            using var resp = await http.PostAsync($"{settings.MoodleBaseUrl}/webservice/rest/server.php", new FormUrlEncodedContent(form), ct);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            try
            {
                ThrowIfError(doc);
                return doc;
            }
            catch (InvalidTokenException) when (attempt == 0)
            {
                doc.Dispose();
                auth.Forget();
                _token = null;
                _token = await auth.LoginAsync(headlessReauth, ct); // may throw LoginRequiredException
            }
        }
    }

    public static void ThrowIfError(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("exception", out _)) return;
        var code = doc.RootElement.TryGetProperty("errorcode", out var c) ? c.GetString() : "";
        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
        if (code == "invalidtoken") throw new InvalidTokenException(msg ?? code);
        throw new HttpRequestException($"Moodle error {code}: {msg}");
    }

    public async Task<long> GetUserIdAsync(CancellationToken ct = default)
    {
        using var d = await CallAsync("core_webservice_get_site_info", null, ct);
        return d.RootElement.GetProperty("userid").GetInt64();
    }

    public async Task<List<CourseDto>> GetCoursesAsync(CancellationToken ct = default)
    {
        using var d = await CallAsync("core_course_get_enrolled_courses_by_timeline_classification",
            [new("classification", "inprogress")], ct);
        return ParseCourses(d);
    }

    public async Task<List<AssignmentDto>> GetAssignmentsAsync(IEnumerable<long> courseIds, CancellationToken ct = default)
    {
        var args = courseIds.Select((id, i) => new KeyValuePair<string, string>($"courseids[{i}]", id.ToString())).ToList();
        if (args.Count == 0) return [];
        using var d = await CallAsync("mod_assign_get_assignments", args, ct);
        return ParseAssignments(d);
    }

    public async Task<SubmissionStatus> GetSubmissionStatusAsync(long assignId, CancellationToken ct = default)
    {
        using var d = await CallAsync("mod_assign_get_submission_status", [new("assignid", assignId.ToString())], ct);
        return ParseSubmissionStatus(d);
    }

    // ---- parsing (public for fixture tests)
    public static List<CourseDto> ParseCourses(JsonDocument d) =>
        d.RootElement.GetProperty("courses").EnumerateArray()
            .Select(c => new CourseDto(c.GetProperty("id").GetInt64(), Str(c, "fullname"), Str(c, "shortname"))).ToList();

    public static List<AssignmentDto> ParseAssignments(JsonDocument d)
    {
        var list = new List<AssignmentDto>();
        foreach (var course in d.RootElement.GetProperty("courses").EnumerateArray())
        {
            var courseId = course.GetProperty("id").GetInt64();
            if (!course.TryGetProperty("assignments", out var arr)) continue;
            foreach (var a in arr.EnumerateArray())
            {
                var atts = new List<AttachmentInfo>();
                if (a.TryGetProperty("introattachments", out var ia))
                    foreach (var f in ia.EnumerateArray())
                        atts.Add(new(Str(f, "filename"), f.TryGetProperty("filesize", out var s) ? s.GetInt64() : 0,
                            f.TryGetProperty("timemodified", out var t) ? t.GetInt64() : 0));
                list.Add(new(a.GetProperty("id").GetInt64(), courseId, a.GetProperty("cmid").GetInt64(), Str(a, "name"),
                    Str(a, "intro"), a.TryGetProperty("duedate", out var dd) ? dd.GetInt64() : 0, atts));
            }
        }
        return list;
    }

    public static SubmissionStatus ParseSubmissionStatus(JsonDocument d)
    {
        var root = d.RootElement;
        var submitted = false;
        if (root.TryGetProperty("lastattempt", out var la) && la.ValueKind == JsonValueKind.Object)
        {
            if (la.TryGetProperty("submission", out var sub) && sub.ValueKind == JsonValueKind.Object)
                submitted = Str(sub, "status") == "submitted";
            if (!submitted && la.TryGetProperty("teamsubmission", out var ts) && ts.ValueKind == JsonValueKind.Object)
                submitted = Str(ts, "status") == "submitted";
        }
        string? grade = null, feedback = null;
        if (root.TryGetProperty("feedback", out var fb) && fb.ValueKind == JsonValueKind.Object)
        {
            if (fb.TryGetProperty("gradefordisplay", out var g)) grade = StripHtml(g.GetString());
            if (fb.TryGetProperty("plugins", out var pl))
                foreach (var p in pl.EnumerateArray())
                    if (p.TryGetProperty("editorfields", out var ef))
                        foreach (var e in ef.EnumerateArray())
                            if (e.TryGetProperty("text", out var tx) && StripHtml(tx.GetString()) is { Length: > 0 } txt)
                                feedback = (feedback is null ? "" : feedback + "\n") + txt;
        }
        return new(submitted, string.IsNullOrWhiteSpace(grade) ? null : grade, feedback);
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string? StripHtml(string? s) =>
        s is null ? null : Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")), @"\s+", " ").Trim();
}
