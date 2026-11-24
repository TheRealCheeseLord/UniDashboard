using System.Text.Json;
using UniDashboard.Core;
using UniDashboard.Core.Univie;

/// <summary>
/// Fills a throwaway database with obviously fake data (used for README screenshots and manual demos).
/// Everything is relative to "now", so the dashboard always looks alive. Nothing here touches the real data folder.
/// </summary>
static class DemoSeed
{
    public const long CourseNum = 9000;

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "data.db");
        foreach (var f in Directory.GetFiles(dir, "data.db*")) File.Delete(f);
        var db = new Database(dbPath);
        var s = new AppSettings(db);
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        DateTimeOffset At(int days, int h, int m = 0) => today.AddDays(days).AddHours(h).AddMinutes(m);

        s.MoodleBaseUrl = "https://moodle.example.org";
        s.StudentName = "Alex Example";
        s.Matrikelnummer = "a12345678";
        s.ShowDemoEvents = false;
        s.SyncIntervalMinutes = 30;
        s.ReminderHours = 24;

        // ---------- Moodle courses + assignments
        var courses = new (long Id, string Name, string Short)[]
        {
            (CourseNum + 1, "Analysis I", "ANA1"), (CourseNum + 2, "Linear Algebra", "LINALG"),
            (CourseNum + 3, "Introduction to Algorithms", "ALGO"), (CourseNum + 4, "Statistics for Natural Sciences", "STAT"),
        };
        foreach (var c in courses) db.UpsertCourse(new Course(c.Id, c.Name, c.Short, true));

        var assignments = new (long Id, int Course, string Name, DateTimeOffset? Due, bool Submitted, string? Grade, string Intro, bool Unseen)[]
        {
            (1, 0, "Exercise sheet 7", now.AddDays(-2), false, null, "<p>Problems 1–5 from the script, chapter 4. Hand in as a single PDF.</p>", false),
            (2, 2, "Programming task: graph search", now.AddHours(20), false, null, "<p>Implement BFS and DFS and compare them on the provided test graphs.</p><p>Upload your <b>source code</b> and a short report.</p>", true),
            (3, 1, "Homework 6", now.AddDays(3).AddHours(2), false, null, "<p>Eigenvalues and eigenvectors: exercises 1–4.</p>", true),
            (4, 3, "Lab report: distributions", now.AddDays(6), false, null, "<p>Describe the sample data set and fit a normal distribution. Max. 3 pages.</p>", false),
            (5, 0, "Exercise sheet 8", now.AddDays(9), false, null, "<p>Series and convergence tests.</p>", false),
            (6, 2, "Midterm project proposal", now.AddDays(16), false, null, "<p>One page: topic, planned approach, milestones.</p>", false),
            (7, 1, "Homework 5", now.AddDays(-9), true, "9 / 10", "<p>Determinants and inverses.</p>", false),
            (8, 3, "Quiz 2", now.AddDays(-5), true, "17 / 20", "<p>Short online quiz.</p>", false),
            (9, 0, "Exercise sheet 6", now.AddDays(-12), true, "8 / 10", "<p>Continuity and limits.</p>", false),
        };
        var atts = JsonSerializer.Serialize(new[] { new AttachmentInfo("sheet.pdf", 184_000, now.AddDays(-14).ToUnixTimeSeconds()) });
        foreach (var a in assignments.Where(x => x.Id != 2))   // #2 gets a change history below
        {
            var asg = new Assignment
            {
                Id = a.Id, CourseId = courses[a.Course].Id, CourseName = courses[a.Course].Name, Cmid = 5000 + a.Id, Name = a.Name, IntroHtml = a.Intro,
                DueAt = a.Due, AttachmentCount = 1, AttachmentsJson = atts, Submitted = a.Submitted, Grade = a.Grade,
                ContentHash = "demo" + a.Id, LastSeenAt = now, UnseenChange = a.Unseen,
            };
            db.SaveAssignment(asg, ChangeKind.New, Snap(asg, a.Intro, a.Due, a.Submitted));
        }
        // One assignment with a visible change history (due date moved, description extended)
        {
            var a = assignments[1];
            var edited = new Assignment
            {
                Id = a.Id, CourseId = courses[a.Course].Id, CourseName = courses[a.Course].Name, Cmid = 5000 + a.Id, Name = a.Name,
                IntroHtml = a.Intro, DueAt = a.Due, AttachmentCount = 1, AttachmentsJson = atts, ContentHash = "demo2-new", LastSeenAt = now, UnseenChange = true,
            };
            var older = a.Due!.Value.AddDays(2);
            db.SaveAssignment(new Assignment
            {
                Id = a.Id, CourseId = edited.CourseId, CourseName = edited.CourseName, Cmid = edited.Cmid, Name = a.Name,
                IntroHtml = "<p>Implement BFS and DFS.</p>", DueAt = older, AttachmentCount = 1, AttachmentsJson = atts, ContentHash = "demo2-old", LastSeenAt = now.AddDays(-5),
            }, ChangeKind.New, Snap(edited, "<p>Implement BFS and DFS.</p>", older, false, now.AddDays(-5)));
            db.SaveAssignment(edited, ChangeKind.DueDateChanged | ChangeKind.DescriptionChanged, Snap(edited, a.Intro, a.Due, false));
        }

        // ---------- u:space enrolments, sessions, exams
        var enrolments = new (string Key, string Lv, string Title, string Type, string Status, int? Waitlist, int Course)[]
        {
            ("100001-1-2026W", "100.001", "Analysis I", "VO", "ANGEMELDET", null, 0),
            ("100002-1-2026W", "100.002", "Analysis I", "UE", "ANGEMELDET", null, 0),
            ("100003-1-2026W", "100.003", "Linear Algebra", "VO", "ANGEMELDET", null, 1),
            ("100004-1-2026W", "100.004", "Introduction to Algorithms", "VU", "ANGEMELDET", null, 2),
            ("100005-1-2026W", "100.005", "Statistics for Natural Sciences", "VO", "WARTELISTE", 3, 3),
        };
        var rooms = new[] { "HS 1, Main Building", "SR 12", "HS 4", "Computer Lab 2", "HS 2" };
        var ei = 0;
        foreach (var e in enrolments)
        {
            var rec = new EnrolmentRecord
            {
                Key = e.Key, LvNr = e.Lv, GroupNr = "1", Semester = "2026W", Title = e.Title, Type = e.Type, Status = e.Status, Immanent = e.Type is "UE" or "VU",
                StudyName = "BSc Demo Studies", MoodleCourseId = courses[e.Course].Id, WaitlistPosition = e.Waitlist,
                LecturersJson = """[{"FirstName":"Pat","LastName":"Professor"}]""", ContentHash = "demo" + e.Key, LastSeenAt = now, Active = true,
            };
            var sessions = new List<SessionRecord>();
            for (var w = -1; w <= 5; w++)
            {
                var day = ei switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 4 } + w * 7;
                var begin = At(day, 8 + ei * 2 % 8, ei % 2 == 0 ? 15 : 0);
                var ss = new SessionRecord
                {
                    Key = SessionDiffer.KeyFor(e.Key, begin, 0), EnrolmentKey = e.Key, Begin = begin, End = begin.AddMinutes(90), Room = rooms[ei],
                    Hash = "h" + e.Key + w, FirstSeenAt = now, LastChangedAt = now,
                };
                if (ei == 0 && w == 1) { ss.Status = SessionStatus.Removed; ss.Change = SessionChange.Removed; }
                if (ei == 2 && w == 1)
                {
                    ss.Change = SessionChange.Moved; ss.PrevBegin = begin; ss.PrevEnd = begin.AddMinutes(90); ss.PrevRoom = ss.Room;
                    ss.Begin = begin.AddDays(2).AddHours(3); ss.End = ss.Begin.AddMinutes(90); ss.Key = SessionDiffer.KeyFor(e.Key, ss.Begin, 0);
                }
                sessions.Add(ss);
            }
            var exams = new List<ExamRecord>();
            if (ei is 0 or 2 or 3)
                foreach (var (offset, idx) in new[] { (25 + ei * 3, 1), (39 + ei * 4, 2), (60 + ei * 2, 3) })
                {
                    var b = At(offset, 10 + idx);
                    exams.Add(new ExamRecord
                    {
                        Key = $"{e.Key}|x{idx}", EnrolmentKey = e.Key, ExtId = "x" + idx, Title = e.Title + " – exam " + idx, Begin = b, End = b.AddHours(2), TimeKnown = true,
                        Room = rooms[(ei + idx) % rooms.Length], EnrollFrom = b.AddDays(-30), EnrollTo = b.AddDays(-3), DisenrollUntil = b.AddDays(-5),
                        Registered = ei == 3 && idx == 2, FirstSeenAt = now, LastChangedAt = now, Hash = "e" + e.Key + idx,
                    });
                }
            db.SaveEnrolment(rec, sessions, JsonSerializer.Serialize(new { e.Title, e.Type, Semester = "2026W", e.Status }), "New", exams);
            ei++;
        }
        Console.WriteLine($"Demo data written to {dir}");
    }

    static string Snap(Assignment a, string intro, DateTimeOffset? due, bool submitted, DateTimeOffset? _ = null) =>
        JsonSerializer.Serialize(new
        {
            a.Name, Course = a.CourseName, IntroHtml = intro, Due = due?.ToUnixTimeSeconds() ?? 0,
            Attachments = new[] { new AttachmentInfo("sheet.pdf", 184_000, 0) }, Submitted = submitted, a.Grade, a.Feedback,
        });
}
