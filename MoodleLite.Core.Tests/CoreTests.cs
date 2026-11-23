using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using MoodleLite.Core;
using MoodleLite.Core.Api;
using MoodleLite.Core.Auth;
using MoodleLite.Core.Docx;
using MoodleLite.Core.Sync;

namespace MoodleLite.Core.Tests;

public class ChangeDetectorTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static long Due(int hours) => T0.AddHours(hours).ToUnixTimeSeconds();

    static FetchedAssignment Base(Action<Dictionary<string, object?>>? mod = null) => new(
        1, 10, 100, "HW 1", "<p>Read chapter 1</p>", Due(48),
        [new("a.pdf", 100, 5), new("b.pdf", 200, 6)], false, null, null);

    static Assignment Stored(FetchedAssignment f) => ChangeDetector.Detect(null, f, "Course", T0).New;

    [Fact]
    public void New_when_no_previous()
    {
        var r = ChangeDetector.Detect(null, Base(), "Course", T0);
        Assert.Equal(ChangeKind.New, r.Kinds);
    }

    [Fact]
    public void No_change_when_identical_even_with_html_whitespace_noise_and_attachment_order()
    {
        var old = Stored(Base());
        var f = Base() with
        {
            IntroHtml = "<p>Read   chapter 1</p>\r\n  ",
            Attachments = [new("b.pdf", 200, 6), new("a.pdf", 100, 5)],
        };
        Assert.Equal(ChangeKind.None, ChangeDetector.Detect(old, f, "Course", T0).Kinds);
    }

    [Fact]
    public void Due_date_changed() =>
        Assert.Equal(ChangeKind.DueDateChanged, ChangeDetector.Detect(Stored(Base()), Base() with { DueUnix = Due(96) }, "Course", T0).Kinds);

    [Fact]
    public void Description_changed() =>
        Assert.Equal(ChangeKind.DescriptionChanged, ChangeDetector.Detect(Stored(Base()), Base() with { IntroHtml = "<p>Read chapter 2</p>" }, "Course", T0).Kinds);

    [Fact]
    public void Grade_or_feedback_changed() =>
        Assert.Equal(ChangeKind.GradeOrFeedback, ChangeDetector.Detect(Stored(Base()), Base() with { Grade = "85,00" }, "Course", T0).Kinds);

    [Fact]
    public void Attachments_changed_is_silent_but_sets_unseen()
    {
        var r = ChangeDetector.Detect(Stored(Base()), Base() with { Attachments = [new("a.pdf", 100, 5)] }, "Course", T0);
        Assert.Equal(ChangeKind.AttachmentsChanged, r.Kinds);
        Assert.True(r.New.UnseenChange);
        var s = new AppSettings(new Database(Path.GetTempFileName()));
        Assert.Empty(NotificationRules.ForChange(r, s));
    }

    [Fact]
    public void Submitted_is_silent_and_does_not_set_unseen()
    {
        var old = Stored(Base()); old.UnseenChange = false;
        var r = ChangeDetector.Detect(old, Base() with { Submitted = true }, "Course", T0);
        Assert.Equal(ChangeKind.Submitted, r.Kinds);
        Assert.False(r.New.UnseenChange);
        var s = new AppSettings(new Database(Path.GetTempFileName()));
        Assert.Empty(NotificationRules.ForChange(r, s));
    }

    [Fact]
    public void Multiple_kinds_combine()
    {
        var r = ChangeDetector.Detect(Stored(Base()), Base() with { DueUnix = Due(5), Grade = "1" }, "Course", T0);
        Assert.Equal(ChangeKind.DueDateChanged | ChangeKind.GradeOrFeedback, r.Kinds);
    }
}

public class NotificationRuleTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static AppSettings NewSettings() => new(new Database(Path.GetTempFileName()));

    static Assignment A(double hoursFromNow, bool submitted = false) => new()
    {
        Id = 1, Name = "HW", CourseName = "C", DueAt = Now.AddHours(hoursFromNow), Submitted = submitted,
    };

    [Fact] public void Reminder_within_24h() => Assert.Single(NotificationRules.DueReminders([A(10)], NewSettings(), Now));
    [Fact] public void No_reminder_after_deadline() => Assert.Empty(NotificationRules.DueReminders([A(-1)], NewSettings(), Now));
    [Fact] public void No_reminder_when_submitted() => Assert.Empty(NotificationRules.DueReminders([A(10, true)], NewSettings(), Now));
    [Fact] public void No_reminder_when_far_away() => Assert.Empty(NotificationRules.DueReminders([A(48)], NewSettings(), Now));

    [Fact]
    public void Reminder_key_is_due_date_so_it_refires_when_deadline_moves()
    {
        var s = NewSettings();
        var k1 = NotificationRules.DueReminders([A(10)], s, Now).Single().Key;
        var k2 = NotificationRules.DueReminders([A(12)], s, Now).Single().Key;
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void Kind_toggle_disables_toast()
    {
        var s = NewSettings();
        s.SetNotify(ChangeKind.DueDateChanged, false);
        var old = ChangeDetector.Detect(null, new(1, 1, 1, "HW", "x", 100, [], false, null, null), "C", Now).New;
        var r = ChangeDetector.Detect(old, new(1, 1, 1, "HW", "x", 200, [], false, null, null), "C", Now);
        Assert.Empty(NotificationRules.ForChange(r, s));
    }
}

public class DatabaseTests
{
    [Fact]
    public void Snapshots_and_dedup_roundtrip()
    {
        var db = new Database(Path.GetTempFileName());
        db.UpsertCourse(new(10, "Course", "C", true));
        var r = ChangeDetector.Detect(null, new(1, 10, 100, "HW", "<p>x</p>", 1000, [], false, null, null), "Course", DateTimeOffset.UtcNow);
        db.SaveAssignment(r.New, r.Kinds, r.SnapshotJson);
        Assert.Single(db.GetSnapshots(1));
        Assert.Equal("HW", db.GetAssignment(1)!.Name);
        Assert.False(db.WasSent(1, "New", "new"));
        db.MarkSent(1, "New", "new", DateTimeOffset.UtcNow);
        Assert.True(db.WasSent(1, "New", "new"));
        db.SetCourseEnabled(10, false);
        Assert.Empty(db.GetAssignments());
    }
}

public class MoodleParsingTests
{
    static JsonDocument Fx(string n) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", n)));

    [Fact] public void Parses_courses() => Assert.NotEmpty(MoodleClient.ParseCourses(Fx("timeline.json")));

    [Fact]
    public void Parses_assignments()
    {
        var l = MoodleClient.ParseAssignments(Fx("assignments.json"));
        Assert.NotEmpty(l);
        Assert.All(l, a => Assert.True(a.Cmid > 0));
    }

    [Fact] public void Status_new_is_not_submitted() => Assert.False(MoodleClient.ParseSubmissionStatus(Fx("submission_status_new.json")).Submitted);
    [Fact] public void Status_submitted() => Assert.True(MoodleClient.ParseSubmissionStatus(Fx("submission_status_submitted.json")).Submitted);

    [Fact]
    public void Status_graded_has_grade()
    {
        var s = MoodleClient.ParseSubmissionStatus(Fx("submission_status_graded.json"));
        Assert.Equal("85,00", s.Grade);
    }

    [Fact]
    public void Invalid_token_error_is_detected() =>
        Assert.Throws<InvalidTokenException>(() => MoodleClient.ThrowIfError(Fx("error_invalidtoken.json")));

    [Fact]
    public void Token_url_is_parsed()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("sig:::the-token:::private"));
        Assert.Equal("the-token", AuthService.ParseTokenUrl("moodlemobile://token=" + Uri.EscapeDataString(payload)));
    }
}

public class DocxTests
{
    [Fact]
    public void Template_is_filled_including_runs_split_across_placeholders_and_header()
    {
        var tpl = Path.Combine(Path.GetTempPath(), $"tpl-{Guid.NewGuid()}.docx");
        TemplateBuilder.Build(tpl);

        // Split "{{Title}}" across runs to simulate Word behaviour
        using (var d = WordprocessingDocument.Open(tpl, true))
        {
            var p = d.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().First();
            var run = p.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>().First();
            var t = run.Elements<DocumentFormat.OpenXml.Wordprocessing.Text>().First();
            t.Text = "{{Ti";
            run.InsertAfterSelf(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("tle}}")));
            d.MainDocumentPart.Document.Save();
        }

        var values = new Dictionary<string, string>
        {
            ["Title"] = "Übung 1", ["Course"] = "Algebra", ["Author"] = "Jane Doe", ["Matrikel"] = "Matrikelnummer 123",
            ["Date"] = "1. November 2026", ["DueDate"] = "Montag", ["MoodleUrl"] = "https://x/y",
            ["Description"] = "Line one\nLine two",
        };
        var bytes = DocxGenerator.Generate(tpl, values);
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Übung 1", body);
        Assert.Contains("Line one", body);
        Assert.Contains("Line two", body);
        Assert.DoesNotContain("{{", body);
        Assert.Contains("Jane Doe · Matrikelnummer 123", doc.MainDocumentPart.HeaderParts.First().Header.InnerText);
        var hdr = doc.MainDocumentPart.HeaderParts.First().Header.InnerText;
        Assert.Contains("1. November 2026", hdr);
        Assert.DoesNotContain("{{", hdr);
        File.Delete(tpl);
    }

    [Fact]
    public void Html_is_converted_to_plain_paragraphs() =>
        Assert.Equal("Hello\nWorld\n• one", DocxGenerator.HtmlToPlainText("<p>Hello</p><p>World &amp;</p>".Replace(" &amp;", "") + "<ul><li>one</li></ul>"));
}
