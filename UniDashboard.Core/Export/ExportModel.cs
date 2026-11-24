using System.Security.Cryptography;
using System.Text;
using UniDashboard.Core.Univie;

namespace UniDashboard.Core.Export;

public enum ExportKind { Session, Exam }

/// <summary>One calendar entry to export (to .ics and to Google Calendar).</summary>
public record ExportItem(
    string Key, ExportKind Kind, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
    string? Location, string Description, string? Url, string? PrevKey = null, string? Lvnr = null, string? GroupNr = null, string? Semester = null, string? CourseTitle = null)
{
    /// <summary>Content hash of everything that is visible in the calendar entry.</summary>
    public string Hash
    {
        get
        {
            var s = string.Join("\u0001", Title, Start.ToUnixTimeSeconds(), End.ToUnixTimeSeconds(), AllDay, Location, Description, Url);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
        }
    }
}

public class ExportOptions
{
    public bool Sessions { get; init; } = true;
    public bool RegisteredExams { get; init; } = true;
    /// <summary>Only items that end after "now" (otherwise everything we know).</summary>
    public bool FromTodayOnly { get; init; } = true;
}

/// <summary>Stable identifiers derived from an item key.</summary>
public static class ExportIds
{
    static byte[] Sha1(string key) => SHA1.HashData(Encoding.UTF8.GetBytes("md-export|" + key));

    /// <summary>iCalendar UID: stable per item, so re-importing the same file updates instead of duplicating.</summary>
    public static string Uid(string key) => "md-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + "@unidashboard";

    /// <summary>Google Calendar event id: base32hex (a–v, 0–9), 5–1024 chars, deterministic → creation is idempotent.</summary>
    public static string GoogleEventId(string key)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuv";
        var bytes = Sha1(key);                       // 20 bytes → 32 base32hex chars
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return "md" + sb;                            // "md" prefix keeps ours recognisable; still valid base32hex
    }
}

public static class ExportBuilder
{
    /// <summary>Builds the export items from the local database (sessions of active enrolments, registered exams).</summary>
    public static List<ExportItem> Build(Database db, ExportOptions options, DateTimeOffset now)
    {
        var from = options.FromTodayOnly ? now : DateTimeOffset.FromUnixTimeSeconds(0);
        var to = DateTimeOffset.FromUnixTimeSeconds(4102444800);   // 2100
        var items = new List<ExportItem>();

        if (options.Sessions)
            foreach (var (s, e) in db.GetSessionsBetween(from, to))
            {
                // GetSessionsBetween also returns rows matched by their previous slot; we want the current slot only.
                if (s.Status != SessionStatus.Active || s.End < from || s.Begin >= to) continue;
                items.Add(SessionItem(s, e));
            }

        if (options.RegisteredExams)
            foreach (var (x, e) in db.GetAllExams().Where(x => x.Exam.Registered && x.Exam.Status == ExamStatus.Active))
            {
                if (x.End < from) continue;
                items.Add(ExamItem(x, e));
            }

        return items.OrderBy(i => i.Start).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
    }

    public static ExportItem SessionItem(SessionRecord s, EnrolmentRecord e)
    {
        var link = UfindLink(e);
        var desc = new List<string> { e.Title, $"{e.LvNr}-{e.GroupNr} · {e.Type} · {e.Semester}" };
        if (!string.IsNullOrWhiteSpace(s.Room)) desc.Add(s.Room!);
        desc.Add(link);
        if (!string.IsNullOrWhiteSpace(e.MoodleUrl)) desc.Add("Moodle: " + e.MoodleUrl);
        return new($"session:{s.Key}", ExportKind.Session, string.IsNullOrWhiteSpace(e.Type) ? e.Title : $"{e.Title} ({e.Type})",
            s.Begin, s.End, false, s.Room, string.Join("\n", desc), link,
            s.Change == SessionChange.Moved && s.PrevKey is { Length: > 0 } pk ? $"session:{pk}" : null,
            e.LvNr, e.GroupNr, e.Semester, e.Title);
    }

    public static ExportItem ExamItem(ExamRecord x, EnrolmentRecord e)
    {
        var link = UfindLink(e);
        var local = x.Begin.ToLocalTime();
        var desc = new List<string> { "Registered exam — " + e.Title, $"{e.LvNr} · {e.Semester}" };
        if (!x.TimeKnown) desc.Add("Exam time not published yet — check u:space.");
        if (!string.IsNullOrWhiteSpace(x.Room)) desc.Add(x.Room!);
        if (!string.IsNullOrWhiteSpace(x.When)) desc.Add(x.When!);
        desc.Add(link);

        DateTimeOffset start = x.Begin, end = x.End;
        if (!x.TimeKnown)
        {
            // All-day: [local midnight, next local midnight)
            var day = local.Date;
            start = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
            var next = day.AddDays(1);
            end = new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next));
        }
        return new($"exam:{x.Key}", ExportKind.Exam, "Exam: " + e.Title, start, end, !x.TimeKnown, x.Room, string.Join("\n", desc), link, null,
            e.LvNr, e.GroupNr, e.Semester, e.Title);
    }

    static string UfindLink(EnrolmentRecord e) =>
        !string.IsNullOrWhiteSpace(e.UfindLink) ? e.UfindLink! : $"https://ufind.univie.ac.at/de/course.html?lv={e.LvNr}&semester={e.Semester}#gr{e.GroupNr}";
}
