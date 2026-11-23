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

    /// <summary>
    /// Time-based exam reminders. "Registration opens": the window started within the last 72 h and is still open.
    /// "Closes soon": within the reminder threshold of the window end, and no exam date of that course is registered yet.
    /// </summary>
    public static IEnumerable<PendingNotification> ExamReminders(
        IEnumerable<(Univie.ExamRecord Exam, Univie.EnrolmentRecord Enrolment)> all, AppSettings s, DateTimeOffset now)
    {
        var list = all.Where(x => x.Exam.Status == Univie.ExamStatus.Active).ToList();
        var registeredCourses = list.Where(x => x.Exam.Registered).Select(x => x.Enrolment.Key).ToHashSet();
        var threshold = TimeSpan.FromHours(s.ReminderHours);
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-AT");

        foreach (var (x, e) in list)
        {
            if (x.Begin < now || !x.HasWindow || registeredCourses.Contains(e.Key)) continue;
            var when = x.TimeKnown ? x.Begin.ToLocalTime().ToString("ddd d.M. HH:mm", de) : x.Begin.ToLocalTime().ToString("ddd d.M.", de);
            var id = Univie.UnivieSyncService.Id(e.Key);

            if (s.NotifyCourse(Univie.CourseKinds.ExamRegOpens) && x.WindowOpen(now) && now - x.EnrollFrom!.Value <= TimeSpan.FromHours(72))
                yield return new(id, Univie.CourseKinds.ExamRegOpens, x.ExtId + "|" + x.EnrollFrom!.Value.ToUnixTimeSeconds(), e.Title,
                    $"Exam registration open for {when} until {x.EnrollTo!.Value.ToLocalTime().ToString("ddd d.M. HH:mm", de)}", e.Key);

            var left = x.EnrollTo!.Value - now;
            if (s.NotifyCourse(Univie.CourseKinds.ExamRegClosing) && x.WindowOpen(now) && left <= threshold)
                yield return new(id, Univie.CourseKinds.ExamRegClosing, x.ExtId + "|" + x.EnrollTo!.Value.ToUnixTimeSeconds(), e.Title,
                    $"Exam registration for {when} closes {x.EnrollTo!.Value.ToLocalTime().ToString("ddd d.M. HH:mm", de)}", e.Key);
        }
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
