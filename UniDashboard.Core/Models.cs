namespace UniDashboard.Core;

public record Course(long Id, string Name, string ShortName, bool Enabled);

public record AttachmentInfo(string Filename, long Size, long TimeModified);

public class Assignment
{
    public long Id { get; set; }
    public long CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public long Cmid { get; set; }
    public string Name { get; set; } = "";
    public string IntroHtml { get; set; } = "";
    public DateTimeOffset? DueAt { get; set; }
    public int AttachmentCount { get; set; }
    public string AttachmentsJson { get; set; } = "[]";
    public bool Submitted { get; set; }
    public string? Grade { get; set; }
    public string? Feedback { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
    public bool UnseenChange { get; set; }
}

[Flags]
public enum ChangeKind
{
    None = 0,
    New = 1,
    DueDateChanged = 2,
    DescriptionChanged = 4,
    GradeOrFeedback = 8,
    AttachmentsChanged = 16,
    Submitted = 32,
}

/// <summary>A calendar event (to be fetched by a future feature; the UI only displays them).</summary>
public record CalendarEvent(
    string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
    string? Location = null, string? Description = null, string? Url = null,
    string Source = "", string? CourseName = null, string Status = "active", string? Note = null);

public record Snapshot(long Id, long AssignmentId, DateTimeOffset TakenAt, string Hash, string Json, ChangeKind Kinds, string Label = "");

/// <summary>Raw fetched data for one assignment, before change detection.</summary>
public record FetchedAssignment(
    long Id, long CourseId, long Cmid, string Name, string IntroHtml, long DueUnix,
    List<AttachmentInfo> Attachments, bool Submitted, string? Grade, string? Feedback);

public record PendingNotification(long AssignmentId, string Kind, string Key, string Title, string Body, string? CourseKey = null);

public class InvalidTokenException(string msg = "invalidtoken") : Exception(msg);
public class LoginRequiredException(string msg) : Exception(msg);
