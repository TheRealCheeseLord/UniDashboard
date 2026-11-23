using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoodleDashboard.Core.Sync;

public record DetectionResult(Assignment New, Assignment? Old, ChangeKind Kinds, string SnapshotJson);

public static partial class ChangeDetector
{
    [GeneratedRegex(@">\s+<")] private static partial Regex BetweenTags();
    [GeneratedRegex(@"\s+")] private static partial Regex Ws();

    /// <summary>Strips HTML whitespace noise so cosmetic re-saves don't count as changes.</summary>
    public static string NormalizeHtml(string? html)
    {
        var s = (html ?? "").Replace("&nbsp;", " ").Replace(" ", " ");
        s = BetweenTags().Replace(s, "><");
        return Ws().Replace(s, " ").Trim();
    }

    public static string NormalizeText(string? s) => Ws().Replace(s ?? "", " ").Trim();

    public static List<AttachmentInfo> NormalizeAttachments(IEnumerable<AttachmentInfo> a) =>
        a.OrderBy(x => x.Filename, StringComparer.Ordinal).ThenBy(x => x.Size).ThenBy(x => x.TimeModified).ToList();

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static string DescriptionHash(string name, string introHtml) => Hash(name + "\u0001" + NormalizeHtml(introHtml));

    public static DetectionResult Detect(Assignment? old, FetchedAssignment f, string courseName, DateTimeOffset now)
    {
        var atts = NormalizeAttachments(f.Attachments);
        var attJson = JsonSerializer.Serialize(atts);
        var intro = NormalizeHtml(f.IntroHtml);
        var grade = NormalizeText(f.Grade) is { Length: > 0 } g ? g : null;
        var feedback = NormalizeText(f.Feedback) is { Length: > 0 } fb ? fb : null;
        DateTimeOffset? due = f.DueUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(f.DueUnix) : null;

        var a = new Assignment
        {
            Id = f.Id, CourseId = f.CourseId, CourseName = courseName, Cmid = f.Cmid, Name = f.Name, IntroHtml = intro,
            DueAt = due, AttachmentCount = atts.Count, AttachmentsJson = attJson, Submitted = f.Submitted,
            Grade = grade, Feedback = feedback, LastSeenAt = now,
        };
        a.ContentHash = Hash(string.Join("\u0002", a.Name, intro, due?.ToUnixTimeSeconds() ?? 0, attJson, a.Submitted, grade, feedback));

        var kinds = ChangeKind.None;
        if (old is null) kinds = ChangeKind.New;
        else if (old.ContentHash != a.ContentHash)
        {
            if ((old.DueAt?.ToUnixTimeSeconds() ?? 0) != (due?.ToUnixTimeSeconds() ?? 0)) kinds |= ChangeKind.DueDateChanged;
            if (DescriptionHash(old.Name, old.IntroHtml) != DescriptionHash(a.Name, a.IntroHtml)) kinds |= ChangeKind.DescriptionChanged;
            if (old.Grade != grade || old.Feedback != feedback) kinds |= ChangeKind.GradeOrFeedback;
            if (NormalizeAttachmentsJson(old.AttachmentsJson) != attJson) kinds |= ChangeKind.AttachmentsChanged;
            if (old.Submitted != a.Submitted) kinds |= ChangeKind.Submitted;
        }

        a.UnseenChange = (old?.UnseenChange ?? false) || (kinds & ~ChangeKind.Submitted) != ChangeKind.None;
        var snap = JsonSerializer.Serialize(new
        {
            a.Name, Course = courseName, IntroHtml = intro, Due = due?.ToUnixTimeSeconds() ?? 0,
            Attachments = atts, a.Submitted, a.Grade, a.Feedback,
        });
        return new(a, old, kinds, snap);
    }

    static string NormalizeAttachmentsJson(string json)
    {
        try { return JsonSerializer.Serialize(NormalizeAttachments(JsonSerializer.Deserialize<List<AttachmentInfo>>(json) ?? [])); }
        catch (JsonException) { return json; }
    }
}
