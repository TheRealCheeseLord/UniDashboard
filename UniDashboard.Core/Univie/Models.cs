namespace UniDashboard.Core.Univie;

/// <summary>A course group the student is enrolled in (u:space), enriched with u:find information.</summary>
public class EnrolmentRecord
{
    public string Key { get; set; } = "";            // lvnr-group-semester, e.g. 010004-1-2026W
    public string LvNr { get; set; } = "";
    public string GroupNr { get; set; } = "";
    public string Semester { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";         // ANGEMELDET, WARTELISTE, ...
    public bool Immanent { get; set; }
    public string? StudyName { get; set; }
    public string? MoodleUrl { get; set; }
    public long? MoodleCourseId { get; set; }
    public string LecturersJson { get; set; } = "[]";
    public int? WaitlistPosition { get; set; }
    public string? UfindLink { get; set; }
    public string? Remark { get; set; }
    public string? UfindVersion { get; set; }
    public string UfindJson { get; set; } = "";      // serialised Ufind.UfindCourse (static info), "" until fetched
    public DateTimeOffset? UfindFetchedAt { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Active { get; set; } = true;         // false once u:space no longer lists the enrolment
    public bool UnseenChange { get; set; }
}

/// <summary>
/// One dated session of an enrolment. u:space gives no session id, so identity is derived:
/// enrolment key + local date + ordinal among that day's sessions.
/// </summary>
public class SessionRecord
{
    public string Key { get; set; } = "";
    public string EnrolmentKey { get; set; } = "";
    public DateTimeOffset Begin { get; set; }
    public DateTimeOffset End { get; set; }
    public string? Room { get; set; }
    public string Hash { get; set; } = "";
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public SessionChange Change { get; set; } = SessionChange.None;   // label for the latest change (moved/changed/added)
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    public DateTimeOffset? PrevBegin { get; set; }
    public DateTimeOffset? PrevEnd { get; set; }
    public string? PrevRoom { get; set; }
    /// <summary>Key of the session this one was moved from (lets exports keep the same calendar event).</summary>
    public string? PrevKey { get; set; }
}

public enum SessionStatus { Active = 0, Removed = 1 }
public enum SessionChange { None = 0, Added = 1, Changed = 2, Moved = 3, Removed = 4 }

public record SessionEvent(SessionChange Change, SessionRecord Session, SessionRecord? Previous);

/// <summary>Course-level notification kinds (stored as strings in notifications_sent / settings).</summary>
public static class CourseKinds
{
    public const string New = "CourseNew";
    public const string Status = "CourseStatus";
    public const string SessionChanged = "SessionChanged";
    public const string SessionMoved = "SessionMoved";
    public const string SessionRemoved = "SessionRemoved";
    public const string SessionAdded = "SessionAdded";
    public const string Info = "CourseInfo";
    public const string ExamChanged = "ExamChanged";
    public const string ExamRegOpens = "ExamRegOpens";
    public const string ExamRegClosing = "ExamRegClosing";

    public static readonly (string Kind, string Label, bool Default)[] All =
    [
        (New, "New course enrolment", true),
        (Status, "Enrolment status changed (waitlist, deregistered…)", true),
        (SessionChanged, "Session time or room changed", true),
        (SessionMoved, "Session moved to another day", true),
        (SessionRemoved, "Session cancelled / removed", true),
        (SessionAdded, "Session added", true),
        (Info, "Course information changed (lecturers, literature…)", false),
        (ExamChanged, "Exam dates: new / changed / removed", true),
        (ExamRegOpens, "Exam registration opens", true),
        (ExamRegClosing, "Exam registration closes soon (not registered yet)", true),
    ];
}
