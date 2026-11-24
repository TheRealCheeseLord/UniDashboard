using System.Text.Json;
using System.Xml.Linq;
using UniDashboard.Core;
using UniDashboard.Core.Sync;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Univie;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Tests;

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
        public List<ExamRegistrationDto> Regs { get; set; } = [];
        public IReadOnlyList<ExamRegistrationDto> LastExamRegistrations => Regs;
        public Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default) => Task.FromResult(List);
    }

    class FakeFind : ICourseInfoSource
    {
        public int Calls;
        public string Version = "v1";
        public string Literature = "Book A";
        public List<UfindExam> Exams = [];
        public Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default)
        {
            Calls++;
            var g = new UfindGroup($"{lvNr}-1", "1", null, 0, false, null, null, null, [], [], new() { ["de"] = Literature }, [], [], [], [], Exams);
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


public class ExamParserAndDifferTests
{
    static System.Xml.Linq.XDocument Fx(string n) => System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", n));
    static DateTimeOffset At(int month, int day, int h = 0) => new(new DateTime(2099, month, day, h, 0, 0, DateTimeKind.Local));

    [Fact]
    public void Parses_all_exam_dates_with_registration_windows()
    {
        var c = UfindClient.ParseCourse(Fx("ufind_course_010033.xml"))!;
        var exams = c.Groups[0].Exams!;
        Assert.Equal(4, exams.Count);
        var first = exams[0];
        Assert.True(first.TimeKnown);
        Assert.NotNull(first.EnrollFrom);
        Assert.NotNull(first.EnrollTo);
        Assert.NotNull(first.DisenrollUntil);
        Assert.True(first.EnrollTo < first.Begin);                 // registration ends before the exam
        Assert.All(exams, e => Assert.False(string.IsNullOrEmpty(e.ExtId)));
        Assert.Equal(exams.Select(e => e.Begin).OrderBy(x => x), exams.Select(e => e.Begin));
    }

    [Fact]
    public void Date_only_exam_has_unknown_time_and_empty_registrations_are_tolerated()
    {
        var latin = UfindClient.ParseCourse(Fx("ufind_course_090034.xml"))!.Groups[0].Exams!;
        var dateOnly = latin.Where(e => !e.TimeKnown).ToList();
        Assert.NotEmpty(dateOnly);
        Assert.Contains(dateOnly, e => e.EnrollFrom is null && e.EnrollTo is null);   // window not published yet
        var oral = UfindClient.ParseCourse(Fx("ufind_course_010011.xml"))!.Groups[0].Exams!.Single();
        Assert.False(oral.TimeKnown);
        Assert.False(string.IsNullOrEmpty(oral.When));
    }

    [Fact]
    public void Courses_without_exams_have_an_empty_list() =>
        Assert.Empty(UfindClient.ParseCourse(Fx("ufind_course_010004.xml"))!.Groups[0].Exams!);

    static ExamRecord E(string ext, DateTimeOffset begin, DateTimeOffset? from = null, DateTimeOffset? to = null) => new()
    {
        Key = "K|" + ext, EnrolmentKey = "K", ExtId = ext, Title = "T", Begin = begin, End = begin.AddHours(1), TimeKnown = true, EnrollFrom = from, EnrollTo = to,
    };

    [Fact]
    public void Differ_first_run_is_silent_and_unchanged_has_no_events()
    {
        var now = At(1, 1);
        var (r1, e1) = ExamDiffer.Diff([], [E("a", At(2, 1))], now, first: true);
        Assert.Empty(e1);
        var (_, e2) = ExamDiffer.Diff(r1, [E("a", At(2, 1))], now.AddDays(1));
        Assert.Empty(e2);
    }

    [Fact]
    public void Differ_detects_added_changed_and_removed_by_extid()
    {
        var now = At(1, 1);
        var (stored, _) = ExamDiffer.Diff([], [E("a", At(2, 1), At(1, 5), At(1, 20)), E("b", At(3, 1))], now, first: true);

        var (res, ev) = ExamDiffer.Diff(stored, [E("a", At(2, 3), At(1, 5), At(1, 22)), E("c", At(4, 1))], now.AddDays(1));
        Assert.Contains(ev, e => e.Change == ExamChange.Changed && e.Exam.ExtId == "a" && e.Previous!.Begin == At(2, 1));
        Assert.Contains(ev, e => e.Change == ExamChange.Added && e.Exam.ExtId == "c");
        Assert.Contains(ev, e => e.Change == ExamChange.Removed && e.Exam.ExtId == "b");
        Assert.Equal(ExamStatus.Removed, res.Single(r => r.ExtId == "b").Status);
        Assert.Equal(At(1, 20), res.Single(r => r.ExtId == "a").PrevEnrollTo);
    }

    [Fact]
    public void Changing_only_the_registered_flag_is_not_an_exam_change()
    {
        var now = At(1, 1);
        var (stored, _) = ExamDiffer.Diff([], [E("a", At(2, 1))], now, first: true);
        var f = E("a", At(2, 1)); f.Registered = true;
        var (res, ev) = ExamDiffer.Diff(stored, [f], now.AddDays(1));
        Assert.Empty(ev);
        Assert.True(res.Single().Registered);
    }

    [Fact]
    public void Window_published_later_counts_as_changed()
    {
        var now = At(1, 1);
        var (stored, _) = ExamDiffer.Diff([], [E("a", At(3, 1))], now, first: true);
        var (_, ev) = ExamDiffer.Diff(stored, [E("a", At(3, 1), At(2, 1), At(2, 15))], now.AddDays(1));
        Assert.Equal(ExamChange.Changed, Assert.Single(ev).Change);
    }
}

public class ExamSyncTests
{
    class Space(List<EnrolmentDto> list) : IEnrolmentSource
    {
        public List<EnrolmentDto> List { get; set; } = list;
        public List<ExamRegistrationDto> Regs { get; set; } = [];
        public IReadOnlyList<ExamRegistrationDto> LastExamRegistrations => Regs;
        public Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default) => Task.FromResult(List);
    }

    class Find : ICourseInfoSource
    {
        public string Version = "v1";
        public List<UfindExam> Exams = [];
        public Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default)
        {
            var g = new UfindGroup($"{lvNr}-1", "1", null, 0, false, null, null, null, [], [], [], [], [], [], [], Exams);
            return Task.FromResult<UfindCourse?>(new(lvNr, semester, Version, "T", "T", "VO", "Vorlesung", 3, 2, false, null, [], [g]));
        }
    }

    class Capture : INotifier
    {
        public List<PendingNotification> Sent { get; } = [];
        public void Send(PendingNotification n) => Sent.Add(n);
    }

    static DateTimeOffset At(int month, int day, int h = 9) => new(new DateTime(2099, month, day, h, 0, 0, DateTimeKind.Local));
    static UfindExam X(string ext, DateTimeOffset begin, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(ext, ext, begin, begin.AddHours(1), "Exam", "Exam", null, [], from, to, to?.AddDays(3));

    static EnrolmentDto Enr() => new("010033", "1", "2099W", "Judentum", "VO", "ANGEMELDET", false, [], [], null, null, null, null, null, null);

    static (UnivieSyncService Svc, Space Space, Find Find, Capture Cap, Database Db) Make()
    {
        var db = new Database(Path.GetTempFileName());
        var space = new Space([Enr()]);
        var find = new Find { Exams = [X("e1", At(2, 1), At(1, 10), At(1, 24)), X("e2", At(3, 1))] };
        var cap = new Capture();
        return (new UnivieSyncService(db, new AppSettings(db), space, find, cap, headless: true) { UfindRefresh = TimeSpan.Zero }, space, find, cap, db);
    }

    [Fact]
    public async Task First_run_stores_all_exam_dates_silently()
    {
        var (svc, _, _, cap, db) = Make();
        await svc.RunAsync();
        var exams = db.GetExams("010033-1-2099W");
        Assert.Equal(2, exams.Count);
        Assert.NotNull(exams.Single(e => e.ExtId == "e1").EnrollTo);
        Assert.Null(exams.Single(e => e.ExtId == "e2").EnrollTo);
        Assert.DoesNotContain(cap.Sent, n => n.Kind == CourseKinds.ExamChanged);
    }

    [Fact]
    public async Task New_exam_date_creates_snapshot_and_one_toast()
    {
        var (svc, _, find, cap, db) = Make();
        await svc.RunAsync();
        find.Version = "v2";
        find.Exams.Add(X("e3", At(4, 1), At(3, 10), At(3, 24)));
        var r = await svc.RunAsync();
        Assert.Equal(1, r.Changed);
        var toast = Assert.Single(cap.Sent, n => n.Kind == CourseKinds.ExamChanged);
        Assert.Contains("New exam date", toast.Body);
        Assert.Equal(3, db.GetExams("010033-1-2099W").Count);
        Assert.Contains("Exams:", UniDashboard.Core.Univie.SnapshotText.Render(db.GetEnrolmentSnapshots("010033-1-2099W")[0].Json));
    }

    [Fact]
    public async Task Moved_exam_date_is_a_change_not_remove_plus_add()
    {
        var (svc, _, find, cap, db) = Make();
        await svc.RunAsync();
        find.Version = "v2";
        find.Exams[0] = X("e1", At(2, 8), At(1, 10), At(1, 24));
        await svc.RunAsync();
        var e1 = db.GetExams("010033-1-2099W").Single(e => e.ExtId == "e1");
        Assert.Equal(ExamChange.Changed, e1.Change);
        Assert.Equal(At(2, 1), e1.PrevBegin);
        Assert.Contains(cap.Sent, n => n.Body.StartsWith("Exam date changed"));
    }

    [Fact]
    public async Task Registration_in_uspace_marks_the_matching_exam_registered_without_a_toast()
    {
        var (svc, space, _, cap, db) = Make();
        await svc.RunAsync();
        space.Regs = [new("010033", "2099W", "e1", At(2, 1), At(2, 1).AddHours(1), "HS 1", "ANGEMELDET", "{\"anmeldungTyp\":\"PRUEFUNG\",\"pruefung\":{\"id\":\"e1\"}}")];
        await svc.RunAsync();
        var exams = db.GetExams("010033-1-2099W");
        Assert.True(exams.Single(e => e.ExtId == "e1").Registered);
        Assert.False(exams.Single(e => e.ExtId == "e2").Registered);
        Assert.DoesNotContain(cap.Sent, n => n.Kind == CourseKinds.ExamChanged);
    }

    [Fact]
    public async Task Registration_for_an_unlisted_date_is_still_shown()
    {
        var (svc, space, _, _, db) = Make();
        await svc.RunAsync();
        space.Regs = [new("010033", "2099W", "zzz", At(5, 5), At(5, 5).AddHours(2), null, "ANGEMELDET", "{}")];
        await svc.RunAsync();
        Assert.Contains(db.GetExams("010033-1-2099W"), e => e.Registered && e.Key.Contains("uspace:zzz"));
    }

    [Fact]
    public async Task Exam_shape_is_remembered_once()
    {
        var (svc, space, _, _, db) = Make();
        var raw = "{\"anmeldungTyp\":\"PRUEFUNG\",\"status\":\"ANGEMELDET\",\"pruefung\":{\"id\":5,\"beginn\":1}}";
        space.Regs = [new("010033", "2099W", "5", null, null, null, "ANGEMELDET", raw)];
        var lines = new List<string>();
        var s2 = new UnivieSyncService(db, new AppSettings(db), space, new Find(), null, true, lines.Add);
        await s2.RunAsync();
        await s2.RunAsync();
        Assert.Single(lines, l => l.Contains("exam registration shape"));
    }

    // ---- reminders
    static EnrolmentRecord Enrol() => new() { Key = "K", Title = "Judentum" };
    static ExamRecord Ex(string ext, DateTimeOffset begin, DateTimeOffset from, DateTimeOffset to, bool reg = false) => new()
    {
        Key = "K|" + ext, EnrolmentKey = "K", ExtId = ext, Begin = begin, End = begin.AddHours(1), TimeKnown = true, EnrollFrom = from, EnrollTo = to, Registered = reg,
    };

    static AppSettings Settings() => new(new Database(Path.GetTempFileName()));

    [Fact]
    public void Registration_opens_toast_only_for_a_recently_opened_window()
    {
        var now = At(1, 10, 12);
        var recent = Ex("a", At(2, 1), now.AddHours(-5), now.AddDays(14));
        var old = Ex("b", At(2, 1), now.AddDays(-8), now.AddDays(6));
        var list = NotificationRules.ExamReminders([(recent, Enrol()), (old, Enrol())], Settings(), now).ToList();
        Assert.Single(list, n => n.Kind == CourseKinds.ExamRegOpens && n.Key.StartsWith("a|"));
        Assert.DoesNotContain(list, n => n.Kind == CourseKinds.ExamRegOpens && n.Key.StartsWith("b|"));
    }

    [Fact]
    public void Closing_soon_within_threshold_and_not_when_registered_or_closed()
    {
        var now = At(1, 10, 12);
        var closing = Ex("a", At(2, 1), now.AddDays(-10), now.AddHours(10));
        Assert.Single(NotificationRules.ExamReminders([(closing, Enrol())], Settings(), now), n => n.Kind == CourseKinds.ExamRegClosing);

        var other = Ex("b", At(2, 8), now.AddDays(-10), now.AddDays(5), reg: true);   // registered for another date of the course
        Assert.Empty(NotificationRules.ExamReminders([(closing, Enrol()), (other, Enrol())], Settings(), now));

        var closed = Ex("c", At(2, 1), now.AddDays(-10), now.AddHours(-1));
        Assert.Empty(NotificationRules.ExamReminders([(closed, Enrol())], Settings(), now));
    }

    [Fact]
    public void Reminder_toggles_are_respected_and_dedup_key_includes_the_window()
    {
        var now = At(1, 10, 12);
        var s = Settings();
        s.SetNotifyCourse(CourseKinds.ExamRegClosing, false);
        var x = Ex("a", At(2, 1), now.AddDays(-10), now.AddHours(10));
        Assert.Empty(NotificationRules.ExamReminders([(x, Enrol())], s, now));
        s.SetNotifyCourse(CourseKinds.ExamRegClosing, true);
        var k1 = NotificationRules.ExamReminders([(x, Enrol())], s, now).Single().Key;
        x.EnrollTo = now.AddHours(20);
        var k2 = NotificationRules.ExamReminders([(x, Enrol())], s, now).Single().Key;
        Assert.NotEqual(k1, k2);   // a moved deadline reminds again
    }
}

public class UspaceExamRegistrationParserTests
{
    [Fact]
    public void Course_items_are_not_exam_registrations_but_exam_items_are()
    {
        var json = """
        [
          {"anmeldungTyp":"LEHRVERANSTALTUNG","lehrveranstaltung":{"lvNr":"010033","semesterKuerzel":"2099W"},"pruefung":null,"status":"ANGEMELDET"},
          {"anmeldungTyp":"PRUEFUNG","status":"ANGEMELDET","semesterKuerzel":"2099W",
           "lehrveranstaltung":{"lvNr":"010033","titel":"X"},
           "pruefung":{"id":1459600,"beginn":4070908800000,"ende":4070914200000,"raumName":"HS 1"}}
        ]
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var regs = UspaceClient.ParseExamRegistrations(doc);
        var r = Assert.Single(regs);
        Assert.Equal("010033", r.LvNr);
        Assert.Equal("2099W", r.Semester);
        Assert.Equal("1459600", r.ExtId);
        Assert.Equal("HS 1", r.Room);
        Assert.NotNull(r.Begin);
        Assert.True(r.End > r.Begin);
        // and the course parser still ignores exam items
        Assert.Single(UspaceClient.ParseEnrolments(doc));
    }

    [Fact]
    public void Shape_contains_keys_and_types_but_no_values()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"a":"secret","b":{"c":1}}""");
        var shape = UspaceClient.ShapeOf(doc.RootElement);
        Assert.Equal("{a:string,b:{c:number}}", shape);
        Assert.DoesNotContain("secret", shape);
    }
}
