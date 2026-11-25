using UniDashboard.Core.Ufind;

namespace UniDashboard.Core.Tests;

public class CurriculumParserTests
{
    static string Fx(string n) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", n));

    [Fact]
    public void ParseSection_builds_hierarchy_with_exams_and_courses()
    {
        var root = CurriculumClient.ParseSection(Fx("ufind_vvz_sub_352175.html"), "352175");
        Assert.Equal("Diplom Katholische Fachtheologie (011 [5] - Version 2026)", root.Title);   // the h1, not the programme leadership from the breadcrumb
        var section = root.Children.Single(c => c.PathId == "352386");
        Assert.StartsWith("Erster Studienabschnitt", section.Title);
        var steop1 = section.Children.Single(c => c.PathId == "352387");
        Assert.StartsWith("STEOP I", steop1.Title);
        var exam = Assert.Single(steop1.Exams);
        Assert.Equal("352387", exam.ModPath);
        Assert.Equal("01-2130", exam.Eq);
        Assert.Contains(steop1.Courses, c => c.LvNr == "010001" && c.Type == "PVU");
        var steop2 = section.Children.Single(c => c.PathId == "352388");
        Assert.Equal(4, steop2.Courses.Count);
        Assert.Contains("VOR-ORT", steop2.Exams.Single().Labels);
    }

    [Fact]
    public void ParseExam_reads_all_dates()
    {
        var ex = CurriculumClient.ParseExam(Fx("ufind_exam_352387.html"), "352387", "01-2130");
        Assert.Equal("Modulprüfung", ex.ExamType);
        Assert.Equal(6m, ex.Ects);
        Assert.Equal(4, ex.Dates.Count);
        var first = ex.Dates[0];
        Assert.Equal(new DateTime(2026, 12, 18, 9, 45, 0), first.Begin.DateTime);
        Assert.Equal(new DateTime(2026, 12, 18, 11, 45, 0), first.End.DateTime);
        Assert.True(first.TimeKnown);
        Assert.StartsWith("sect:", first.ExtId);
    }
}
