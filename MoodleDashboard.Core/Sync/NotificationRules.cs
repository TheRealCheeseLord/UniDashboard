namespace MoodleDashboard.Core.Sync;

public static class NotificationRules
{
    static string Fmt(DateTimeOffset? d) => d is null ? "no due date" : d.Value.ToLocalTime().ToString("g");

    /// <summary>Toasts triggered by a detected change; respects per-kind toggles.</summary>
    public static IEnumerable<PendingNotification> ForChange(DetectionResult r, AppSettings s)
    {
        var a = r.New;
        var kinds = r.Kinds;
        if (kinds.HasFlag(ChangeKind.New))
        {
            if (s.Notify(ChangeKind.New))
                yield return new(a.Id, "New", "new", "New assignment", $"New: {a.Name} ({a.CourseName}) — due {Fmt(a.DueAt)}");
            yield break; // a brand-new assignment only gets the "New" toast
        }
        if (kinds.HasFlag(ChangeKind.DueDateChanged) && s.Notify(ChangeKind.DueDateChanged))
            yield return new(a.Id, "DueDateChanged", (a.DueAt?.ToUnixTimeSeconds() ?? 0).ToString(), a.Name,
                $"Due date changed: {Fmt(r.Old?.DueAt)} → {Fmt(a.DueAt)} ({a.CourseName})");
        if (kinds.HasFlag(ChangeKind.DescriptionChanged) && s.Notify(ChangeKind.DescriptionChanged))
            yield return new(a.Id, "DescriptionChanged", ChangeDetector.DescriptionHash(a.Name, a.IntroHtml), a.Name,
                $"Description updated ({a.CourseName})");
        if (kinds.HasFlag(ChangeKind.GradeOrFeedback) && s.Notify(ChangeKind.GradeOrFeedback))
            yield return new(a.Id, "GradeOrFeedback", ChangeDetector.Hash(a.Grade + "|" + a.Feedback), a.Name,
                a.Grade is { } g ? $"Graded: {g} ({a.CourseName})" : $"New feedback ({a.CourseName})");
        // Silent by default; only toast if the user switched the kind on in settings.
        if (kinds.HasFlag(ChangeKind.AttachmentsChanged) && s.Notify(ChangeKind.AttachmentsChanged))
            yield return new(a.Id, "AttachmentsChanged", ChangeDetector.Hash(a.AttachmentsJson), a.Name,
                $"Attachments changed ({a.CourseName})");
        if (kinds.HasFlag(ChangeKind.Submitted) && s.Notify(ChangeKind.Submitted))
            yield return new(a.Id, "Submitted", a.Submitted.ToString(), a.Name,
                a.Submitted ? $"Submitted ({a.CourseName})" : $"Submission withdrawn ({a.CourseName})");
    }

    /// <summary>One reminder when due within threshold, not submitted, not past due. Key = due date.</summary>
    public static IEnumerable<PendingNotification> DueReminders(IEnumerable<Assignment> all, AppSettings s, DateTimeOffset now)
    {
        if (!s.NotifyDueReminder) yield break;
        var threshold = TimeSpan.FromHours(s.ReminderHours);
        foreach (var a in all)
        {
            if (a.Submitted || a.DueAt is not { } due) continue;
            var left = due - now;
            if (left <= TimeSpan.Zero || left > threshold) continue;
            yield return new(a.Id, "DueReminder", due.ToUnixTimeSeconds().ToString(), a.Name,
                $"Due soon: {a.Name} ({a.CourseName}) — {Fmt(due)}");
        }
    }
}
