using UniDashboard.Core;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Univie;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Tests;

public class CurriculumMatcherTests
{
    static UfindChapter Ch(string path, string cat) => new(path, cat, "", "");

    const string Diplom2026 = "Diplom Katholische Fachtheologie (011 [5] - Version 2026)";
    const string Diplom2015 = "Diplom Katholische Fachtheologie (011 [3] - Version 2015) - auslaufend";
    const string Lehramt = "Bachelor Lehramt UF Katholische Religion (193 055, 198 418 - Version 2017) - auslaufend";

    [Fact]
    public void ParseCategory_reads_codes_version_and_discontinued()
    {
        var c = CurriculumMatcher.ParseCategory("1|2|343284", Diplom2015)!;
        Assert.Equal("343284", c.RootPath);
        Assert.Equal(2015, c.VersionYear);
        Assert.True(c.Discontinued);
        var g = Assert.Single(c.Groups);
        Assert.Equal("3", g.Zusatz);
        Assert.Contains("011", g.Codes);

        var l = CurriculumMatcher.ParseCategory("1|2|343261", Lehramt)!;
        Assert.Contains(l.Groups, x => x.Codes.Contains("193") && x.Codes.Contains("055"));
    }

    [Fact]
    public void Select_picks_the_study_version_not_other_programmes_or_old_versions()
    {
        var chapters = new[]
        {
            Ch("1|2|343284|9|8", Diplom2015),
            Ch("1|2|352175|6|5", Diplom2026),
            Ch("1|2|343261|4", Lehramt),
        };
        var study = new StudyDto("Diplom", "ORDENTLICH", ["011"], ["5"], 2026, 1);
        Assert.Equal("352175", Assert.Single(CurriculumMatcher.Select([study], chapters)).Curriculum.RootPath);

        // Without the version mark: newest non-discontinued version that is not newer than the start year.
        var noMark = new StudyDto("Diplom", "ORDENTLICH", ["011"], [], 2026, 1);
        Assert.Equal("352175", Assert.Single(CurriculumMatcher.Select([noMark], chapters)).Curriculum.RootPath);
    }

    [Fact]
    public void Select_matches_multi_code_studies_and_ignores_unrelated()
    {
        var chapters = new[] { Ch("1|2|343261|4", Lehramt), Ch("1|2|352175|6|5", Diplom2026) };
        var lehramt = new StudyDto("UF", "ORDENTLICH", ["193", "055"], [], 2017, null);
        Assert.Equal("343261", Assert.Single(CurriculumMatcher.Select([lehramt], chapters)).Curriculum.RootPath);
        Assert.Empty(CurriculumMatcher.Select([new StudyDto("x", "", ["999"], [], 2020, null)], chapters));
    }
}

public class StudyParserTests
{
    [Fact]
    public void ParseStudies_reads_codes_version_mark_and_start_year()
    {
        const string json = """
            [{"abschnitte":[{"abschnittsNummer":1,"studienelement":{"bezeichnung":"x","extId":1,"kennzahl":"011","studierenVon":1790805600000,"zusatzkennung":"5"}}],
              "extStudienrichtungId":1,"extStudienverlaufId":77,"kennzahl1":"011","kennzahl2":null,"kennzahl3":null,"studienStatus":"ORDENTLICH",
              "studienbeginn":1790805600000,"studienrichtungBezeichnung":"Fachtheologie"}]
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var s = Assert.Single(UspaceClient.ParseStudies(doc));
        Assert.Equal(["011"], s.Kennzahlen);
        Assert.Equal(["5"], s.Zusatz);
        Assert.Equal(2026, s.BeginYear);
        Assert.Equal("ORDENTLICH", s.Status);
    }

    [Fact]
    public void ParseStudies_tolerates_garbage()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"x":1}""");
        Assert.Empty(UspaceClient.ParseStudies(doc));
        using var doc2 = System.Text.Json.JsonDocument.Parse("""[1,{"kennzahl1":null}]""");
        Assert.Empty(UspaceClient.ParseStudies(doc2));
    }

    [Fact]
    public void Course_xml_keeps_chapter_path_ids()
    {
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ufind_course_010004.xml"));
        var c = UfindClient.ParseCourse(doc)!;
        Assert.NotNull(c.Chapters);
        var ch = Assert.Single(c.Chapters!, x => x.Path == "341250|343208|343284|343285|343297");
        Assert.Contains("011 [3]", ch.Category);
    }
}

public class SectionExamSyncTests
{
    class Space(List<EnrolmentDto> list, List<StudyDto> studies) : IEnrolmentSource
    {
        public List<ExamRegistrationDto> Regs { get; set; } = [];
        public IReadOnlyList<ExamRegistrationDto> LastExamRegistrations => Regs;
        public IReadOnlyList<StudyDto> LastStudies => studies;
        public Task<List<EnrolmentDto>> GetEnrolmentsAsync(bool headless, CancellationToken ct = default) => Task.FromResult(list);
    }

    class Find(string category) : ICourseInfoSource
    {
        public Task<UfindCourse?> GetCourseAsync(string lvNr, string semester, CancellationToken ct = default) =>
            Task.FromResult<UfindCourse?>(new(lvNr, semester, "v1", "T", "T", "VO", "Vorlesung", 3, 2, false, null, [], [],
                [new("1|2|352175|352386|352387", category, "", "STEOP I")]));
    }

    class Plan : ICurriculumSource
    {
        public int TreeCalls;
        public List<UfindExam> Dates = [];
        public Task<CurriculumNode?> GetTreeAsync(string pathId, string semester, CancellationToken ct = default)
        {
            TreeCalls++;
            var root = new CurriculumNode { PathId = pathId, Title = "Diplom", Schema = CurriculumClient.CurrentSchema };
            root.Children.Add(new CurriculumNode { PathId = "352387", Title = "STEOP I", Courses = [new("010001", "PVU", "EinfÃ¼hrung")], Exams = [new("352387", "01-2130", "EinfÃ¼hrung", [])] });
            root.Children.Add(new CurriculumNode { PathId = "352500", Title = "FTH 01", Courses = [new("999999", "VO", "x")], Exams = [new("352500", "01-9", "x", [])] });
            return Task.FromResult<CurriculumNode?>(root);
        }
        public Task<CurriculumExam?> GetExamAsync(string modPath, string eq, string semester, CancellationToken ct = default) =>
            Task.FromResult<CurriculumExam?>(new(modPath, eq, "EinfÃ¼hrung in die Theologie", "ModulprÃ¼fung", 6, Dates));
    }

    const string Cat = "Diplom Katholische Fachtheologie (011 [5] - Version 2026)";

    static UfindExam D(string id, int month, int day) =>
        new($"sect:{id}", id, new DateTimeOffset(new DateTime(2099, month, day, 9, 45, 0, DateTimeKind.Local)),
            new DateTimeOffset(new DateTime(2099, month, day, 11, 45, 0, DateTimeKind.Local)), "T", "T", "HS 6", [], null, null, null);

    static EnrolmentDto Enr() => new("010001", "1", "2099W", "EinfÃ¼hrung", "PVU", "ANGEMELDET", false, [], [], null, null, null, null, null, null);
    static StudyDto Study() => new("Diplom", "ORDENTLICH", ["011"], ["5"], 2026, 1);

    static (UnivieSyncService Svc, Space Space, Plan Plan, Database Db) Make(string category = Cat, TimeSpan? treeRefresh = null)
    {
        var db = new Database(Path.GetTempFileName());
        var space = new Space([Enr()], [Study()]);
        var plan = new Plan { Dates = [D("1", 12, 18), D("2", 12, 20)] };
        var svc = new UnivieSyncService(db, new AppSettings(db), space, new Find(category), null, headless: true, null, plan)
        { TreeRefresh = treeRefresh ?? TimeSpan.FromHours(6) };
        return (svc, space, plan, db);
    }

    [Fact]
    public async Task Imports_exams_of_sections_with_an_enrolled_course_only()
    {
        var (svc, _, _, db) = Make();
        await svc.RunAsync();
        var exams = db.GetAllExams();
        Assert.Equal(2, exams.Count);
        Assert.All(exams, x => { Assert.True(x.Enrolment.IsSection); Assert.Equal("STEOP I", x.Enrolment.Title); });
        Assert.Single(db.GetEnrolments());                                   // sections are not mixed into the course list
        Assert.Single(db.GetCurriculumTrees());
    }

    [Fact]
    public async Task Other_programme_is_ignored()
    {
        var (svc, _, plan, db) = Make("Bachelor Geschichte (033 603 - Version 2020)");
        await svc.RunAsync();
        Assert.Empty(db.GetAllExams());
        Assert.Equal(0, plan.TreeCalls);
    }

    [Fact]
    public async Task Tree_is_cached_and_registration_is_matched_by_date()
    {
        var (svc, space, plan, db) = Make();
        space.Regs = [new("", "2099W", "", new DateTimeOffset(new DateTime(2099, 12, 20, 9, 45, 0, DateTimeKind.Local)), null, null, "ANGEMELDET", "{}")];
        await svc.RunAsync();
        await svc.RunAsync();
        Assert.Equal(1, plan.TreeCalls);
        var reg = Assert.Single(db.GetAllExams(), x => x.Exam.Registered);
        Assert.Equal("sect:2", reg.Exam.ExtId);
    }

    [Fact]
    public async Task Moved_date_is_picked_up_and_an_empty_fetch_keeps_the_data()
    {
        var (svc, _, plan, db) = Make(treeRefresh: TimeSpan.Zero);
        await svc.RunAsync();
        plan.Dates = [D("1", 12, 18), D("2", 12, 22)];
        await svc.RunAsync();
        Assert.Contains(db.GetAllExams(), x => x.Exam.ExtId == "sect:2" && x.Exam.Begin.ToLocalTime().Day == 22);
        plan.Dates = [];
        await svc.RunAsync();
        Assert.Equal(2, db.GetAllExams().Count(x => x.Exam.Status == ExamStatus.Active));
    }
}
