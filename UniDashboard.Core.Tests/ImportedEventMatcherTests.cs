using System.Globalization;
using System.Text.RegularExpressions;
using UniDashboard.Core.Export;

namespace UniDashboard.Core.Tests;

public class ImportedEventMatcherTests
{
    static readonly TimeZoneInfo Vienna = Find();
    static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }

    static DateTimeOffset ParseLocal(string yyyymmddThhmmss)
    {
        var dt = DateTime.ParseExact(yyyymmddThhmmss, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        return new DateTimeOffset(dt, Vienna.GetUtcOffset(dt));
    }

    /// <summary>Reads the real u:find iCal fixture exactly like Google would store it (title/start/end/location; random UIDs).</summary>
    static List<RemoteEvent> RealUfindImport(string calendarName = "Studium")
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ufind_ww_010004.ics")).Replace("\r\n ", "").Replace("\n ", "");
        var list = new List<RemoteEvent>();
        foreach (var block in raw.Split("BEGIN:VEVENT").Skip(1))
        {
            string F(string name) => Regex.Match(block, $@"^{name}[^:\r\n]*:(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim('\r', ' ');
            list.Add(new("cal1", calendarName, "g" + list.Count, F("UID"), F("SUMMARY").Replace("\\,", ","),
                ParseLocal(F("DTSTART")), ParseLocal(F("DTEND")), false, F("LOCATION"), null, "etag", "2026-10-01T00:00:00Z"));
        }
        return list;
    }

    /// <summary>Our desired items for the same course (same slots), as the exporter would build them.</summary>
    static List<ExportItem> Desired(IEnumerable<RemoteEvent> slots) => slots.Select((r, i) =>
        new ExportItem($"session:010004-1-2026W|{i}", ExportKind.Session, "Einführung in das wissenschaftliche Arbeiten (PS)", r.Start!.Value, r.End!.Value, false,
            r.Location, "d", "https://ufind.univie.ac.at/de/course.html?lv=010004&semester=2026W#gr1", null, "010004", "1", "2026W", "Einführung in das wissenschaftliche Arbeiten")).ToList();

    [Fact]
    public void Real_ufind_ical_import_is_recognised_as_strong_match_for_every_event()
    {
        var remote = RealUfindImport();
        Assert.Equal(7, remote.Count);
        var matches = ImportedEventMatcher.Match(remote, Desired(remote));
        Assert.Equal(7, matches.Count);
        Assert.All(matches, m => Assert.Equal(MatchConfidence.Strong, m.Confidence));
        Assert.Equal(7, matches.Select(m => m.ItemKey).Distinct().Count());
    }

    [Fact]
    public void Random_uids_play_no_role()
    {
        var a = RealUfindImport();
        var b = a.Select(r => r with { ICalUid = Guid.NewGuid().ToString() }).ToList();
        Assert.Equal(7, ImportedEventMatcher.Match(b, Desired(a)).Count);
    }

    [Fact]
    public void Different_start_time_is_not_a_match()
    {
        var remote = RealUfindImport();
        var desired = Desired(remote).Select(i => i with { Start = i.Start.AddHours(1), End = i.End.AddHours(1) }).ToList();
        Assert.Empty(ImportedEventMatcher.Match(remote, desired));
    }

    [Fact]
    public void One_minute_difference_still_matches_but_ten_minutes_does_not()
    {
        var remote = RealUfindImport();
        var near = Desired(remote).Select(i => i with { Start = i.Start.AddMinutes(1) }).ToList();
        Assert.Equal(7, ImportedEventMatcher.Match(remote, near).Count);
        var far = Desired(remote).Select(i => i with { Start = i.Start.AddMinutes(10) }).ToList();
        Assert.Empty(ImportedEventMatcher.Match(remote, far));
    }

    [Fact]
    public void Our_own_events_are_never_old_imports()
    {
        var remote = RealUfindImport().Select(r => r with { Managed = true, ManagedKey = "x" }).ToList();
        Assert.Empty(ImportedEventMatcher.Match(remote, Desired(remote)));
    }

    [Fact]
    public void Cancelled_remote_events_are_ignored()
    {
        var remote = RealUfindImport().Select(r => r with { Cancelled = true }).ToList();
        Assert.Empty(ImportedEventMatcher.Match(remote, Desired(remote)));
    }

    [Fact]
    public void Unrelated_events_at_the_same_time_are_not_matched()
    {
        var remote = RealUfindImport().Select(r => r with { Summary = "Dentist", Location = "Somewhere" }).ToList();
        Assert.Empty(ImportedEventMatcher.Match(remote, Desired(remote)));
    }

    [Fact]
    public void Title_and_time_match_is_probable()
    {
        var remote = RealUfindImport().Select(r => r with { Summary = "PS Einführung in das wissenschaftliche Arbeiten (Uni)" }).ToList();
        var matches = ImportedEventMatcher.Match(remote, Desired(remote));
        Assert.Equal(7, matches.Count);
        Assert.All(matches, m => Assert.Equal(MatchConfidence.Probable, m.Confidence));
    }

    [Fact]
    public void Ufind_link_in_the_description_with_same_start_is_a_strong_match()
    {
        var remote = RealUfindImport().Select(r => r with { Summary = "Seminar", Description = "https://ufind.univie.ac.at/de/course.html?lv=010004&semester=2026W#gr1" }).ToList();
        var matches = ImportedEventMatcher.Match(remote, Desired(remote));
        Assert.Equal(7, matches.Count);
        Assert.All(matches, m => Assert.Equal(MatchConfidence.Strong, m.Confidence));
    }

    [Fact]
    public void Wrong_group_with_ufind_pattern_is_not_strong()
    {
        var remote = RealUfindImport().Select(r => r with { Summary = r.Summary.Replace("010004-1", "010004-2") }).ToList();
        Assert.DoesNotContain(ImportedEventMatcher.Match(remote, Desired(remote)), m => m.Confidence == MatchConfidence.Strong);
    }

    [Fact]
    public void Duplicate_imports_in_several_calendars_are_all_reported()
    {
        var a = RealUfindImport("Studium");
        var b = RealUfindImport("Uni").Select(r => r with { CalendarId = "cal2" }).ToList();
        var matches = ImportedEventMatcher.Match(a.Concat(b), Desired(a));
        Assert.Equal(14, matches.Count);
        Assert.Equal(2, matches.Select(m => m.Remote.CalendarId).Distinct().Count());
    }

    [Fact]
    public void All_day_exam_items_match_on_the_start_date_only()
    {
        var day = new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));
        var item = new ExportItem("exam:x", ExportKind.Exam, "Exam: Judentum", day, day.AddDays(1), true, null, "d", null, null, "010033", "1", "2099W", "Judentum");
        var remote = new RemoteEvent("c", "C", "i", null, "Prüfung Judentum", day, day.AddDays(1), true, null, null, "e", null);
        Assert.Single(ImportedEventMatcher.Match([remote], [item]));
    }
}
