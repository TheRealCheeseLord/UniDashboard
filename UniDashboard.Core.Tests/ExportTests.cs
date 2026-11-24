using System.Text;
using UniDashboard.Core;
using UniDashboard.Core.Export;
using UniDashboard.Core.Univie;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Tests;

public class IcsWriterTests
{
    static readonly DateTimeOffset Now = new(2099, 1, 1, 8, 0, 0, TimeSpan.Zero);
    static ExportItem Item(string key = "session:k1", string title = "Algebra (VO)", string? loc = "HS 1, Hauptgebäude", bool allDay = false, string desc = "Line1\nLine2") =>
        new(key, ExportKind.Session, title, new DateTimeOffset(2099, 2, 3, 9, 15, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2099, 2, 3, 10, 45, 0, TimeSpan.FromHours(1)),
            allDay, loc, desc, "https://ufind.univie.ac.at/de/course.html?lv=1&semester=2099W#gr1");

    static List<string> Unfold(string ics) =>
        ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.None).ToList();

    [Fact]
    public void Escape_handles_special_characters()
    {
        Assert.Equal("a\\,b\\;c\\nd\\\\e", IcsWriter.Escape("a,b;c\nd\\e"));
        Assert.Equal("x\\ny", IcsWriter.Escape("x\r\ny"));
    }

    [Fact]
    public void Output_is_crlf_terminated_and_no_physical_line_exceeds_75_octets()
    {
        var ics = IcsWriter.Write([Item(title: string.Concat(Enumerable.Repeat("Übung zur Einführung in die Algebra – ", 8)), desc: string.Concat(Enumerable.Repeat("äöüß€ ", 60)))], _ => 0, Now);
        Assert.EndsWith("\r\n", ics);
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
        foreach (var line in ics.Split("\r\n")) Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
    }

    [Fact]
    public void Folding_round_trips_multibyte_text()
    {
        var title = string.Concat(Enumerable.Repeat("ä€𝄞", 40));
        var ics = IcsWriter.Write([Item(title: title)], _ => 0, Now);
        Assert.Contains("SUMMARY:" + title, Unfold(ics));
    }

    [Fact]
    public void Timed_events_use_utc_and_all_day_events_use_dates_with_exclusive_end()
    {
        var timed = Item();
        var allDay = Item("exam:x", "Exam: X", null, allDay: true) with
        {
            Start = new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.FromHours(1)), End = new DateTimeOffset(2099, 3, 2, 0, 0, 0, TimeSpan.FromHours(1)),
        };
        var lines = Unfold(IcsWriter.Write([timed, allDay], _ => 0, Now));
        Assert.Contains("DTSTART:20990203T081500Z", lines);
        Assert.Contains("DTEND:20990203T094500Z", lines);
        Assert.Contains("DTSTART;VALUE=DATE:20990301", lines);
        Assert.Contains("DTEND;VALUE=DATE:20990302", lines);
    }

    [Fact]
    public void Uid_is_stable_per_key_and_distinct_between_keys()
    {
        var a1 = Unfold(IcsWriter.Write([Item("session:a")], _ => 0, Now)).Single(l => l.StartsWith("UID:"));
        var a2 = Unfold(IcsWriter.Write([Item("session:a")], _ => 0, Now.AddDays(5))).Single(l => l.StartsWith("UID:"));
        var b = Unfold(IcsWriter.Write([Item("session:b")], _ => 0, Now)).Single(l => l.StartsWith("UID:"));
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.EndsWith("@unidashboard", a1);
    }

    [Fact]
    public void Text_fields_are_escaped_in_the_output()
    {
        var lines = Unfold(IcsWriter.Write([Item(title: "A, B; C", desc: "x\ny")], _ => 0, Now));
        Assert.Contains("SUMMARY:A\\, B\\; C", lines);
        Assert.Contains("DESCRIPTION:x\\ny", lines);
    }

    [Fact]
    public void Alarm_is_only_added_to_timed_events()
    {
        var allDay = Item("exam:x", allDay: true);
        var ics = IcsWriter.Write([Item(), allDay], _ => 0, Now, alarmMinutes: 30);
        Assert.Single(Unfold(ics), l => l == "BEGIN:VALARM");
        Assert.Contains("TRIGGER:-PT30M", Unfold(ics));
        Assert.DoesNotContain("BEGIN:VALARM", IcsWriter.Write([Item()], _ => 0, Now, alarmMinutes: 0));
    }

    [Fact]
    public void Sequence_stays_for_unchanged_items_and_rises_for_changed_ones()
    {
        var a = Item("session:a");
        var (s1, save1) = IcsWriter.NextSequences([a], new Dictionary<string, (string, int)>());
        Assert.Equal(0, s1["session:a"]);
        var state = save1.ToDictionary(x => x.Key, x => (x.Hash, x.Seq));

        var (s2, save2) = IcsWriter.NextSequences([a], state);
        Assert.Equal(0, s2["session:a"]);
        Assert.Empty(save2);

        var changed = a with { Location = "HS 2" };
        var (s3, save3) = IcsWriter.NextSequences([changed], state);
        Assert.Equal(1, s3["session:a"]);
        Assert.Single(save3);
    }

    [Fact]
    public void Google_event_ids_are_valid_base32hex_stable_and_distinct()
    {
        var id = ExportIds.GoogleEventId("session:a");
        Assert.Matches("^[0-9a-v]{5,1024}$", id);
        Assert.Equal(id, ExportIds.GoogleEventId("session:a"));
        Assert.NotEqual(id, ExportIds.GoogleEventId("session:b"));
    }
}

public class ExportBuilderTests
{
    static DateTimeOffset At(int month, int day, int h = 9) => new(new DateTime(2099, month, day, h, 0, 0, DateTimeKind.Local));
    static readonly DateTimeOffset Now = At(1, 1, 8);

    static (Database Db, EnrolmentRecord Enr) Setup()
    {
        var db = new Database(Path.GetTempFileName());
        var enr = new EnrolmentRecord
        {
            Key = "010004-1-2099W", LvNr = "010004", GroupNr = "1", Semester = "2099W", Title = "Einführung", Type = "PS", Status = "ANGEMELDET",
            MoodleUrl = "https://moodle.univie.ac.at/course/view.php?id=1", LastSeenAt = Now, Active = true,
        };
        return (db, enr);
    }

    static SessionRecord Session(string key, DateTimeOffset begin, SessionStatus st = SessionStatus.Active, SessionChange ch = SessionChange.None, string? prevKey = null) => new()
    {
        Key = key, EnrolmentKey = "010004-1-2099W", Begin = begin, End = begin.AddMinutes(90), Room = "Seminarraum 5", Hash = key, Status = st, Change = ch, PrevKey = prevKey,
        FirstSeenAt = Now, LastChangedAt = Now,
    };

    static ExamRecord Exam(string id, DateTimeOffset begin, bool timeKnown, bool registered, ExamStatus st = ExamStatus.Active) => new()
    {
        Key = "010004-1-2099W|" + id, EnrolmentKey = "010004-1-2099W", ExtId = id, Title = "Einführung", Begin = begin, End = timeKnown ? begin.AddHours(2) : begin,
        TimeKnown = timeKnown, Registered = registered, Status = st, FirstSeenAt = Now, LastChangedAt = Now, Hash = id,
    };

    [Fact]
    public void Sessions_are_exported_removed_ones_are_not_and_moved_ones_carry_the_old_key()
    {
        var (db, enr) = Setup();
        db.SaveEnrolment(enr, [
            Session("S|1", At(2, 3)),
            Session("S|2", At(2, 10), SessionStatus.Removed, SessionChange.Removed),
            Session("S|3", At(2, 18), ch: SessionChange.Moved, prevKey: "S|old"),
        ], null, null);

        var items = ExportBuilder.Build(db, new ExportOptions { RegisteredExams = false }, Now);
        Assert.Equal(["session:S|1", "session:S|3"], items.Select(i => i.Key).ToArray());
        var moved = items.Single(i => i.Key == "session:S|3");
        Assert.Equal("session:S|old", moved.PrevKey);
        Assert.Equal("Einführung (PS)", moved.Title);
        Assert.Equal("Seminarraum 5", moved.Location);
        Assert.Contains("lv=010004", moved.Url);
        Assert.Contains("Moodle:", moved.Description);
    }

    [Fact]
    public void Only_registered_active_exams_are_exported_and_unknown_time_becomes_all_day()
    {
        var (db, enr) = Setup();
        db.SaveEnrolment(enr, [], null, null, [
            Exam("e1", At(3, 1, 11), timeKnown: true, registered: true),
            Exam("e2", At(3, 8, 0), timeKnown: false, registered: true),
            Exam("e3", At(3, 15, 10), timeKnown: true, registered: false),
            Exam("e4", At(3, 22, 10), timeKnown: true, registered: true, st: ExamStatus.Removed),
        ]);
        var items = ExportBuilder.Build(db, new ExportOptions { Sessions = false }, Now);
        Assert.Equal(2, items.Count);
        var timed = items.Single(i => !i.AllDay);
        Assert.StartsWith("Exam: ", timed.Title);
        var allDay = items.Single(i => i.AllDay);
        Assert.Contains("not published", allDay.Description);
        Assert.Equal(TimeSpan.FromDays(1), allDay.End - allDay.Start);
    }

    [Fact]
    public void From_today_only_skips_past_items()
    {
        var (db, enr) = Setup();
        db.SaveEnrolment(enr, [Session("S|past", At(1, 1, 6)), Session("S|future", At(2, 3))], null, null);
        Assert.Equal(["session:S|future"], ExportBuilder.Build(db, new ExportOptions { RegisteredExams = false }, Now).Select(i => i.Key).ToArray());
        Assert.Equal(2, ExportBuilder.Build(db, new ExportOptions { RegisteredExams = false, FromTodayOnly = false }, Now).Count);
    }

    [Fact]
    public void Inactive_enrolments_are_not_exported()
    {
        var (db, enr) = Setup();
        enr.Active = false;
        db.SaveEnrolment(enr, [Session("S|1", At(2, 3))], null, null);
        Assert.Empty(ExportBuilder.Build(db, new ExportOptions { RegisteredExams = false }, Now));
    }
}

public class SessionPrevKeyTests
{
    const string K = "010004-1-2099W";
    static readonly DateTimeOffset Now = new(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static DateTimeOffset At(int day, int h) => new(new DateTime(2099, 2, day, h, 0, 0, DateTimeKind.Local));
    static SessionDto S(int day, int h) => new(At(day, h), At(day, h).AddMinutes(90), "R1");

    [Fact]
    public void Moved_session_remembers_the_key_it_came_from_and_keeps_it_while_unchanged()
    {
        var old = SessionDiffer.Diff([], K, [S(10, 9), S(17, 9)], Now, first: true).Result;
        var oldKey = old.Single(o => o.Begin == At(17, 9)).Key;

        var (res, _) = SessionDiffer.Diff(old, K, [S(10, 9), S(19, 9)], Now.AddHours(1));
        var moved = res.Single(r => r.Change == SessionChange.Moved);
        Assert.Equal(oldKey, moved.PrevKey);

        var (res2, ev2) = SessionDiffer.Diff(res, K, [S(10, 9), S(19, 9)], Now.AddHours(2));
        Assert.Empty(ev2);
        Assert.Equal(oldKey, res2.Single(r => r.Change == SessionChange.Moved).PrevKey);
    }

    [Fact]
    public void Prev_key_survives_a_database_round_trip()
    {
        var db = new Database(Path.GetTempFileName());
        var enr = new EnrolmentRecord { Key = K, LvNr = "010004", Title = "T", LastSeenAt = Now };
        var s = new SessionRecord { Key = "k2", EnrolmentKey = K, Begin = At(19, 9), End = At(19, 10), Hash = "h", PrevKey = "k1", Change = SessionChange.Moved, FirstSeenAt = Now, LastChangedAt = Now };
        db.SaveEnrolment(enr, [s], null, null);
        Assert.Equal("k1", db.GetSessions(K).Single().PrevKey);
    }

    [Fact]
    public void Export_state_round_trips()
    {
        var db = new Database(Path.GetTempFileName());
        db.SaveExportState([("a", "h1", 0), ("b", "h2", 3)]);
        db.SaveExportState([("a", "h1b", 1)]);
        var st = db.GetExportState();
        Assert.Equal(("h1b", 1), st["a"]);
        Assert.Equal(("h2", 3), st["b"]);
    }
}
