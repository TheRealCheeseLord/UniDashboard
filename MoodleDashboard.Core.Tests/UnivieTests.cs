using System.Text.Json;
using System.Xml.Linq;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Sync;
using MoodleDashboard.Core.Ufind;
using MoodleDashboard.Core.Univie;
using MoodleDashboard.Core.Uspace;

namespace MoodleDashboard.Core.Tests;

public class SessionDifferTests
{
    const string K = "010004-1-2026W";
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static DateTimeOffset At(int day, int h, int m = 0) { var d = new DateTime(2026, 11, day, h, m, 0, DateTimeKind.Local); return new(d); }
    static SessionDto S(int day, int h, int dur = 90, string? room = "R1") => new(At(day, h), At(day, h).AddMinutes(dur), room);

    static List<SessionRecord> Store(params SessionDto[] s) => SessionDiffer.Diff([], K, s, Now, first: true).Result;

    [Fact]
    public void First_sync_is_silent()
    {
        var (res, ev) = SessionDiffer.Diff([], K, [S(10, 9), S(17, 9)], Now, first: true);
        Assert.Equal(2, res.Count);
        Assert.Empty(ev);
        Assert.All(res, r => Assert.Equal(SessionChange.None, r.Change));
    }

    [Fact]
    public void Unchanged_produces_no_events()
    {
        var old = Store(S(10, 9), S(17, 9));
        var (_, ev) = SessionDiffer.Diff(old, K, [S(10, 9), S(17, 9)], Now.AddHours(1));
        Assert.Empty(ev);
    }

    [Fact]
    public void Time_change_on_the_same_day_is_changed_in_place()
    {
        var old = Store(S(10, 9), S(17, 9));
        var (res, ev) = SessionDiffer.Diff(old, K, [S(10, 10), S(17, 9)], Now.AddHours(1));
        var e = Assert.Single(ev);
        Assert.Equal(SessionChange.Changed, e.Change);
        Assert.Equal(At(10, 9), e.Previous!.Begin);
        Assert.Equal(At(10, 10), e.Session.Begin);
        Assert.Equal(At(10, 9), res.Single(r => r.Begin == At(10, 10)).PrevBegin);
    }

    [Fact]
    public void Room_change_is_changed_in_place()
    {
        var old = Store(S(10, 9));
        var (res, ev) = SessionDiffer.Diff(old, K, [S(10, 9, room: "R2")], Now);
        Assert.Equal(SessionChange.Changed, Assert.Single(ev).Change);
        Assert.Equal("R1", res.Single().PrevRoom);
    }

    [Fact]
    public void Move_to_another_day_is_paired_as_moved_not_removed_plus_added()
    {
        var old = Store(S(10, 9), S(17, 9));
        var (res, ev) = SessionDiffer.Diff(old, K, [S(10, 9), S(19, 9)], Now.AddHours(1));
        var e = Assert.Single(ev);
        Assert.Equal(SessionChange.Moved, e.Change);
        Assert.Equal(At(17, 9), e.Previous!.Begin);
        Assert.Equal(At(19, 9), e.Session.Begin);
        Assert.Equal(2, res.Count);
        Assert.All(res, r => Assert.Equal(SessionStatus.Active, r.Status));
    }

    [Fact]
    public void Removed_session_is_kept_as_tombstone()
    {
        var old = Store(S(10, 9), S(17, 9));
        var (res, ev) = SessionDiffer.Diff(old, K, [S(10, 9)], Now.AddHours(1));
        Assert.Equal(SessionChange.Removed, Assert.Single(ev).Change);
        var t = res.Single(r => r.Begin == At(17, 9));
        Assert.Equal(SessionStatus.Removed, t.Status);
    }

    [Fact]
    public void Tombstone_survives_further_syncs_and_is_purged_after_a_week()
    {
        var old = Store(S(10, 9), S(17, 9));
        var (r1, _) = SessionDiffer.Diff(old, K, [S(10, 9)], Now);
        var (r2, ev2) = SessionDiffer.Diff(r1, K, [S(10, 9)], Now.AddDays(1));
        Assert.Empty(ev2);
        Assert.Contains(r2, r => r.Status == SessionStatus.Removed);
        var (r3, _) = SessionDiffer.Diff(r2, K, [S(10, 9)], new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain(r3, r => r.Status == SessionStatus.Removed);
    }

    [Fact]
    public void Added_session_is_reported()
    {
        var old = Store(S(10, 9));
        var (_, ev) = SessionDiffer.Diff(old, K, [S(10, 9), S(24, 9)], Now);
        Assert.Equal(SessionChange.Added, Assert.Single(ev).Change);
    }

    [Fact]
    public void Two_sessions_on_one_day_have_distinct_keys()
    {
        var r = Store(S(10, 9), S(10, 14));
        Assert.Equal(2, r.Select(x => x.Key).Distinct().Count());
    }

    [Fact]
    public void Different_counts_pair_the_closest_similar_sessions()
    {
        var old = Store(S(10, 9), S(17, 9), S(24, 9));
        // 17th moves to 18th, 24th disappears
        var (_, ev) = SessionDiffer.Diff(old, K, [S(10, 9), S(18, 9)], Now);
        Assert.Contains(ev, e => e.Change == SessionChange.Moved && e.Session.Begin == At(18, 9));
        Assert.Contains(ev, e => e.Change == SessionChange.Removed && e.Session.Begin == At(24, 9));
    }
}

public class UfindParserTests
{
    static XDocument Fx(string n) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", n));

    [Fact]
    public void Parses_course_with_group_and_moodle_link()
    {
        var c = UfindClient.ParseCourse(Fx("ufind_course_010004.xml"))!;
        Assert.Equal("010004", c.LvNr);
        Assert.Equal("2026W", c.Semester);
        Assert.Equal("2026-08-31T10:25:37+02:00", c.Version);
        Assert.Equal("Introduction to Scientific Work", c.TitleEn);
        Assert.Equal(3.00m, c.Ects);
        var g = Assert.Single(c.Groups);
        Assert.Equal("010004-1", g.GroupId);
        Assert.Equal("1029577", g.RegisterId);
        Assert.Contains("view.php?id=469301", g.MoodleUrl);
        Assert.NotNull(g.EnrollFrom);
        Assert.NotNull(g.DisenrollUntil);
        Assert.Contains("Patrick Rohs", g.Lecturers);
        Assert.NotEmpty(g.Literature);
        Assert.NotEmpty(c.Curricula);
    }

    [Fact]
    public void Tolerates_courses_with_exams_and_missing_platform()
    {
        var c = UfindClient.ParseCourse(Fx("ufind_course_090034.xml"))!;
        Assert.Equal("090034", c.LvNr);
        Assert.Null(c.Groups[0].MoodleUrl);
    }

    [Fact]
    public void Non_course_xml_returns_null() =>
        Assert.Null(UfindClient.ParseCourse(XDocument.Parse("<result/>")));
}

public class UspaceParserTests
{
    static JsonDocument Fx() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "uspace_anmeldungen.json")));

    [Fact]
    public void Parses_courses_sessions_and_moodle_id_and_skips_exams()
    {
        var l = UspaceClient.ParseEnrolments(Fx());
        Assert.Equal(2, l.Count);
        var a = l[0];
        Assert.Equal("010004-1-2026W", a.Key);
        Assert.Equal(2, a.Sessions.Count);
        Assert.Equal("Seminarraum 5 Schenkenstraße 1.OG", a.Sessions[0].Room);
        Assert.Equal(469301, a.MoodleCourseId);
        Assert.Equal("ANGEMELDET", a.Status);
        Assert.Equal(4, l[1].WaitlistPosition);
        Assert.Null(l[1].MoodleUrl);
    }
}

public class UnivieSyncTests
{
    class FakeSpace(List<EnrolmentDto> list) : IEnrolmentSource
    {
        public List<EnrolmentDto> List { get; set; } = list;
        public Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default) => Task.FromResult(List);
    }

    class FakeFind : ICourseInfoSource
    {
        public int Calls;
        public string Version = "v1";
        public string Literature = "Book A";
        public Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default)
        {
            Calls++;
            var g = new UfindGroup($"{lvNr}-1", "1", null, 0, false, null, null, null, [], [], new() { ["de"] = Literature }, [], [], [], []);
            return Task.FromResult<UfindCourse?>(new(lvNr, semester, Version, "T", "T", "VO", "Vorlesung", 3, 2, false, null, [], [g]));
        }
    }

    class Capture : INotifier
    {
        public List<PendingNotification> Sent { get; } = [];
        public void Send(PendingNotification n) => Sent.Add(n);
    }

    static DateTimeOffset At(int day, int h) => new(new DateTime(2099, 11, day, h, 0, 0, DateTimeKind.Local));
    static EnrolmentDto Enr(string status = "ANGEMELDET", params SessionDto[] s) =>
        new("010004", "1", "2099W", "Seminar", "PS", status, true, [], s.ToList(), null, null, null, null, null, null);
    static SessionDto Ses(int day, int h, string room = "R1") => new(At(day, h), At(day, h).AddMinutes(90), room);

    static (UnivieSyncService Svc, FakeSpace Space, FakeFind Find, Capture Cap, Database Db) Make()
    {
        var db = new Database(Path.GetTempFileName());
        var space = new FakeSpace([Enr("ANGEMELDET", Ses(10, 9), Ses(17, 9))]);
        var find = new FakeFind();
        var cap = new Capture();
        return (new UnivieSyncService(db, new AppSettings(db), space, find, cap, headless: true), space, find, cap, db);
    }

    [Fact]
    public async Task First_run_stores_everything_silently()
    {
        var (svc, _, _, cap, db) = Make();
        var r = await svc.RunAsync();
        Assert.Equal(1, r.Enrolments);
        Assert.Empty(cap.Sent);
        Assert.Equal(2, db.GetSessions("010004-1-2099W").Count);
        Assert.NotEmpty(db.GetEnrolment("010004-1-2099W")!.UfindJson);
    }

    [Fact]
    public async Task Unchanged_second_run_has_no_changes_and_no_new_ufind_fetch()
    {
        var (svc, _, find, cap, db) = Make();
        await svc.RunAsync();
        var r = await svc.RunAsync();
        Assert.Equal(0, r.Changed);
        Assert.Empty(cap.Sent);
        Assert.Equal(1, find.Calls);   // refreshed at most every 6 hours
        Assert.Empty(db.GetEnrolmentSnapshots("010004-1-2099W").Skip(1));
    }

    [Fact]
    public async Task Moved_session_creates_snapshot_toast_and_ghost_data()
    {
        var (svc, space, _, cap, db) = Make();
        await svc.RunAsync();
        space.List = [Enr("ANGEMELDET", Ses(10, 9), Ses(19, 9))];
        var r = await svc.RunAsync();
        Assert.Equal(1, r.Changed);
        var toast = Assert.Single(cap.Sent);
        Assert.Equal(CourseKinds.SessionMoved, toast.Kind);
        Assert.Equal("010004-1-2099W", toast.CourseKey);
        var moved = db.GetSessions("010004-1-2099W").Single(s => s.Change == SessionChange.Moved);
        Assert.Equal(At(17, 9), moved.PrevBegin);
        Assert.Equal(2, db.GetEnrolmentSnapshots("010004-1-2099W").Count);
        Assert.True(db.GetEnrolment("010004-1-2099W")!.UnseenChange);
    }

    [Fact]
    public async Task Same_change_is_not_toasted_twice()
    {
        var (svc, space, _, cap, _) = Make();
        await svc.RunAsync();
        space.List = [Enr("ANGEMELDET", Ses(10, 9))];
        await svc.RunAsync();
        await svc.RunAsync();
        Assert.Single(cap.Sent);
    }

    [Fact]
    public async Task Disabled_kind_does_not_toast_but_still_records()
    {
        var (svc, space, _, cap, db) = Make();
        new AppSettings(db).SetNotifyCourse(CourseKinds.SessionRemoved, false);
        await svc.RunAsync();
        space.List = [Enr("ANGEMELDET", Ses(10, 9))];
        await svc.RunAsync();
        Assert.Empty(cap.Sent);
        Assert.Contains(db.GetSessions("010004-1-2099W"), s => s.Status == SessionStatus.Removed);
    }

    [Fact]
    public async Task Status_change_and_deregistration_are_detected()
    {
        var (svc, space, _, cap, db) = Make();
        await svc.RunAsync();
        space.List = [Enr("WARTELISTE", Ses(10, 9), Ses(17, 9))];
        await svc.RunAsync();
        Assert.Contains(cap.Sent, n => n.Kind == CourseKinds.Status);

        space.List = [new("010099", "1", "2099W", "Other", "VO", "ANGEMELDET", false, [], [], null, null, null, null, null, null)];
        await svc.RunAsync();
        Assert.False(db.GetEnrolment("010004-1-2099W")!.Active);
        Assert.Contains(cap.Sent, n => n.Body.StartsWith("No longer enrolled"));
    }

    [Fact]
    public async Task Empty_enrolment_list_does_not_deactivate_everything()
    {
        var (svc, space, _, _, db) = Make();
        await svc.RunAsync();
        space.List = [];
        await svc.RunAsync();
        Assert.True(db.GetEnrolment("010004-1-2099W")!.Active);
    }
}
