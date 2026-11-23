using MoodleDashboard.Core.Api;
using MoodleDashboard.Core.Auth;

namespace MoodleDashboard.Core.Sync;

public interface INotifier
{
    void Send(PendingNotification n);
}

public record SyncResult(int Assignments, int Changed, int Notified, string? Error = null, bool LoginRequired = false);

/// <summary>One sync pass: courses → assignments → submission status → change detection → notifications.</summary>
public class SyncService(Database db, AppSettings settings, MoodleClient client, INotifier? notifier, Action<string>? log = null)
{
    /// <summary>Optional u:space/u:find sync, run after the Moodle sync; its failures never affect the Moodle result.</summary>
    public Univie.UnivieSyncService? Univie { get; set; }

    public async Task<SyncResult> RunAsync(CancellationToken ct = default)
    {
        var r = await RunMoodleAsync(ct);
        if (Univie is null) return r;
        try
        {
            var u = await Univie.RunAsync(ct);
            r = r with { Changed = r.Changed + u.Changed, Notified = r.Notified + u.Notified };
            log?.Invoke($"univie: {u.Enrolments} enrolments, {u.Changed} changed, {u.Notified} toasts" + (u.Error is null ? "" : ", error: " + u.Error));
            if (u.Error is not null && r.Error is null) r = r with { Error = "u:space: " + u.Error, LoginRequired = u.LoginRequired };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke("univie sync crashed: " + e);
            r = r with { Error = r.Error ?? "u:space: " + e.Message };
        }
        return r;
    }

    async Task<SyncResult> RunMoodleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (settings.UserId == 0) settings.UserId = await client.GetUserIdAsync(ct);

            foreach (var c in await client.GetCoursesAsync(ct))
                db.UpsertCourse(new(c.Id, c.FullName, c.ShortName, true));

            var courses = db.GetCourses(enabledOnly: true);
            var names = courses.ToDictionary(c => c.Id, c => c.Name);
            var isInitial = db.GetAssignments().Count == 0;

            var dtos = await client.GetAssignmentsAsync(courses.Select(c => c.Id), ct);
            int changed = 0, notified = 0;

            foreach (var dto in dtos)
            {
                ct.ThrowIfCancellationRequested();
                var old = db.GetAssignment(dto.Id);
                SubmissionStatus st;
                try { st = await client.GetSubmissionStatusAsync(dto.Id, ct); }
                catch (Exception e) when (e is HttpRequestException)
                {
                    log?.Invoke($"submission status failed for {dto.Id}: {e.Message}");
                    st = new(old?.Submitted ?? false, old?.Grade, old?.Feedback);
                }

                var fetched = new FetchedAssignment(dto.Id, dto.CourseId, dto.Cmid, dto.Name, dto.Intro, dto.DueUnix,
                    dto.Attachments, st.Submitted, st.Grade, st.Feedback);
                var r = ChangeDetector.Detect(old, fetched, names.GetValueOrDefault(dto.CourseId, ""), now);
                db.SaveAssignment(r.New, r.Kinds, r.SnapshotJson);
                if (r.Kinds != ChangeKind.None) changed++;

                if (!isInitial) notified += Dispatch(NotificationRules.ForChange(r, settings), now);
            }

            notified += Dispatch(NotificationRules.DueReminders(db.GetAssignments(), settings, now), now);
            settings.LastSync = now;
            return new(dtos.Count, changed, notified);
        }
        catch (LoginRequiredException e)
        {
            log?.Invoke("login required: " + e.Message);
            return new(0, 0, 0, e.Message, LoginRequired: true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log?.Invoke("sync failed: " + e.Message);
            return new(0, 0, 0, e.Message);
        }
    }

    int Dispatch(IEnumerable<PendingNotification> pending, DateTimeOffset now)
    {
        var n = 0;
        foreach (var p in pending)
        {
            if (db.WasSent(p.AssignmentId, p.Kind, p.Key)) continue;
            notifier?.Send(p);
            db.MarkSent(p.AssignmentId, p.Kind, p.Key, now);
            n++;
        }
        return n;
    }
}
