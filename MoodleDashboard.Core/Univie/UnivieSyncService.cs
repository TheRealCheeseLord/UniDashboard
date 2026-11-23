using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MoodleDashboard.Core.Sync;
using MoodleDashboard.Core.Ufind;
using MoodleDashboard.Core.Uspace;

namespace MoodleDashboard.Core.Univie;

public record UnivieSyncResult(int Enrolments, int Changed, int Notified, string? Error = null, bool LoginRequired = false);

/// <summary>
/// u:space (which groups am I enrolled in, with their dated sessions) is the source of truth;
/// u:find adds the static course information and is refetched at most every <see cref="UfindRefresh"/>.
/// Every change (info, status, session moved/changed/removed/added) is snapshotted and optionally toasted.
/// </summary>
public class UnivieSyncService(
    Database db, AppSettings settings, IEnrolmentSource uspace, ICourseInfoSource ufind, INotifier? notifier, bool headless, Action<string>? log = null)
{
    public static readonly TimeSpan UfindRefresh = TimeSpan.FromHours(6);
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    public async Task<UnivieSyncResult> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        List<EnrolmentDto> fresh;
        try { fresh = await uspace.GetEnrolmentsAsync(headless, ct); }
        catch (LoginRequiredException e) { log?.Invoke("u:space login required: " + e.Message); return new(0, 0, 0, e.Message, true); }
        catch (Exception e) when (e is HttpRequestException or Microsoft.Playwright.PlaywrightException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { log?.Invoke("u:space failed: " + e.Message); return new(0, 0, 0, e.Message); }

        var initial = db.GetEnrolments(activeOnly: false).Count == 0;
        int changed = 0, notified = 0;

        foreach (var dto in fresh)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (c, n) = await ProcessAsync(dto, initial, now, ct);
                changed += c; notified += n;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or System.Xml.XmlException)
            {
                log?.Invoke($"course {dto.Key} failed: {e.Message}");   // keep the previous data, carry on
            }
        }

        // Enrolments u:space no longer lists. Guard: an empty list may be an API hiccup.
        if (fresh.Count > 0)
        {
            var seen = fresh.Select(f => f.Key).ToHashSet();
            foreach (var gone in db.GetEnrolments().Where(e => !seen.Contains(e.Key)))
            {
                gone.Active = false; gone.UnseenChange = true; gone.LastSeenAt = now;
                db.SaveEnrolment(gone, null, Snapshot(gone, db.GetSessions(gone.Key)), "NoLongerEnrolled");
                changed++;
                if (settings.NotifyCourse(CourseKinds.Status))
                    notified += Send(new(Id(gone.Key), CourseKinds.Status, "gone", gone.Title, $"No longer enrolled in {gone.Title}", gone.Key), now);
            }
        }
        return new(fresh.Count, changed, notified);
    }

    async Task<(int Changed, int Notified)> ProcessAsync(EnrolmentDto dto, bool initial, DateTimeOffset now, CancellationToken ct)
    {
        var old = db.GetEnrolment(dto.Key);
        var rec = new EnrolmentRecord
        {
            Key = dto.Key, LvNr = dto.LvNr, GroupNr = dto.GroupNr, Semester = dto.Semester, Title = dto.Title, Type = dto.Type, Status = dto.Status,
            Immanent = dto.Immanent, StudyName = dto.StudyName, MoodleUrl = dto.MoodleUrl, MoodleCourseId = dto.MoodleCourseId,
            LecturersJson = JsonSerializer.Serialize(dto.Lecturers), WaitlistPosition = dto.WaitlistPosition, UfindLink = dto.UfindLink,
            Remark = dto.Remark, LastSeenAt = now, Active = true,
            UfindVersion = old?.UfindVersion, UfindJson = old?.UfindJson ?? "", UfindFetchedAt = old?.UfindFetchedAt,
        };

        // u:find: only when stale; keep the old data if the fetch fails or the course version is unchanged.
        if (old?.UfindFetchedAt is not { } at || now - at > UfindRefresh || string.IsNullOrEmpty(old.UfindJson))
        {
            try
            {
                var course = await ufind.GetCourseAsync(dto.LvNr, dto.Semester, ct);
                if (course is not null)
                {
                    rec.UfindFetchedAt = now;
                    if (course.Version != old?.UfindVersion || string.IsNullOrEmpty(old?.UfindJson))
                    {
                        rec.UfindVersion = course.Version;
                        rec.UfindJson = JsonSerializer.Serialize(course);
                    }
                }
            }
            catch (HttpRequestException e) { log?.Invoke($"u:find {dto.LvNr}/{dto.Semester} failed: {e.Message}"); }
        }

        rec.ContentHash = HashInfo(rec);
        var oldSessions = db.GetSessions(dto.Key);
        var (sessions, events) = SessionDiffer.Diff(oldSessions, dto.Key, dto.Sessions, now, first: old is null);

        var infoChanged = old is not null && old.ContentHash != rec.ContentHash;
        var statusChanged = old is not null && (old.Status != rec.Status || old.WaitlistPosition != rec.WaitlistPosition || !old.Active);
        var anyChange = old is null || infoChanged || events.Count > 0;

        rec.UnseenChange = (old?.UnseenChange ?? false) || (old is not null && (infoChanged || events.Count > 0));

        if (!anyChange) { db.SaveEnrolment(rec, null, null, null); return (0, 0); }

        var kinds = new List<string>();
        if (old is null) kinds.Add("New");
        if (statusChanged) kinds.Add("Status");
        if (infoChanged && !statusChanged) kinds.Add("Info");
        kinds.AddRange(events.GroupBy(e => e.Change).Select(g => $"{g.Key}×{g.Count()}"));
        db.SaveEnrolment(rec, sessions, Snapshot(rec, sessions), string.Join(", ", kinds));

        var sent = 0;
        if (!initial)
        {
            if (old is null && settings.NotifyCourse(CourseKinds.New))
                sent += Send(new(Id(dto.Key), CourseKinds.New, "new", rec.Title, $"Enrolled: {rec.Title} ({rec.Type}, {rec.Semester})", dto.Key), now);
            if (statusChanged && settings.NotifyCourse(CourseKinds.Status))
                sent += Send(new(Id(dto.Key), CourseKinds.Status, $"{rec.Status}|{rec.WaitlistPosition}", rec.Title,
                    $"Status: {old!.Status} → {rec.Status}" + (rec.WaitlistPosition is { } w ? $" (waitlist #{w})" : ""), dto.Key), now);
            if (infoChanged && !statusChanged && settings.NotifyCourse(CourseKinds.Info))
                sent += Send(new(Id(dto.Key), CourseKinds.Info, rec.ContentHash, rec.Title, "Course information updated", dto.Key), now);
            if (old is not null) sent += NotifySessions(rec, events, now);
        }
        return (1, sent);
    }

    int NotifySessions(EnrolmentRecord rec, List<SessionEvent> events, DateTimeOffset now)
    {
        var sent = 0;
        foreach (var g in events.GroupBy(e => e.Change))
        {
            var kind = g.Key switch
            {
                SessionChange.Changed => CourseKinds.SessionChanged,
                SessionChange.Moved => CourseKinds.SessionMoved,
                SessionChange.Removed => CourseKinds.SessionRemoved,
                SessionChange.Added => CourseKinds.SessionAdded,
                _ => null,
            };
            if (kind is null || !settings.NotifyCourse(kind)) continue;
            // Past sessions changing are not worth a toast.
            var list = g.Where(e => e.Session.Begin > now.AddHours(-1)).ToList();
            if (list.Count == 0) continue;
            var first = list[0];
            var what = g.Key switch
            {
                SessionChange.Changed => $"changed: {Describe(first.Previous!)} → {Describe(first.Session)}",
                SessionChange.Moved => $"moved: {Describe(first.Previous!)} → {Describe(first.Session)}",
                SessionChange.Removed => $"cancelled: {Describe(first.Session)}",
                _ => $"added: {Describe(first.Session)}",
            };
            var body = list.Count == 1 ? $"Session {what}" : $"{list.Count} sessions {g.Key.ToString().ToLowerInvariant()} (first {what})";
            var key = Hash(string.Join("|", list.Select(e => e.Session.Key + e.Session.Hash)));
            sent += Send(new(Id(rec.Key), kind, key, rec.Title, body, rec.Key), now);
        }
        return sent;
    }

    static string Describe(SessionRecord s) =>
        $"{s.Begin.ToLocalTime().ToString("ddd d.M. HH:mm", De)}" + (string.IsNullOrWhiteSpace(s.Room) ? "" : $" ({ShortRoom(s.Room)})");

    static string ShortRoom(string room) => room.Length > 40 ? room[..40] + "…" : room;

    int Send(PendingNotification p, DateTimeOffset now)
    {
        if (db.WasSent(p.AssignmentId, p.Kind, p.Key)) return 0;
        notifier?.Send(p);
        db.MarkSent(p.AssignmentId, p.Kind, p.Key, now);
        return 1;
    }

    /// <summary>Stable negative id (notifications_sent keeps assignment ids positive).</summary>
    public static long Id(string enrolmentKey) =>
        -(long)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(enrolmentKey)), 0) & 0x7fffffff) - 1;

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    static string HashInfo(EnrolmentRecord r) =>
        Hash(string.Join("\u0001", r.Title, r.Type, r.Status, r.WaitlistPosition, r.LecturersJson, r.MoodleUrl, r.Remark, r.StudyName, r.UfindJson));

    public static string Snapshot(EnrolmentRecord r, IEnumerable<SessionRecord> sessions) => JsonSerializer.Serialize(new
    {
        r.Title, r.Type, r.Status, r.Semester, r.WaitlistPosition, r.StudyName, r.MoodleUrl, r.Remark, r.Active,
        Lecturers = JsonSerializer.Deserialize<List<LecturerDto>>(r.LecturersJson) ?? [],
        Ufind = string.IsNullOrEmpty(r.UfindJson) ? null : JsonSerializer.Deserialize<UfindCourse>(r.UfindJson),
        Sessions = sessions.Select(s => new
        {
            Begin = s.Begin.ToUnixTimeSeconds(), End = s.End.ToUnixTimeSeconds(), s.Room, Status = s.Status.ToString(), Change = s.Change.ToString(),
            PrevBegin = s.PrevBegin?.ToUnixTimeSeconds(), s.PrevRoom,
        }),
    });
}
