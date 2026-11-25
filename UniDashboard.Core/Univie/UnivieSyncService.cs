using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniDashboard.Core.Sync;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Univie;

public record UnivieSyncResult(int Enrolments, int Changed, int Notified, string? Error = null, bool LoginRequired = false);

/// <summary>
/// u:space (which groups am I enrolled in, with their dated sessions) is the source of truth;
/// u:find adds the static course information and is refetched at most every <see cref="UfindRefresh"/>.
/// Every change (info, status, session moved/changed/removed/added) is snapshotted and optionally toasted.
/// </summary>
public partial class UnivieSyncService(
    Database db, AppSettings settings, IEnrolmentSource uspace, ICourseInfoSource ufind, INotifier? notifier, bool headless, Action<string>? log = null,
    ICurriculumSource? curriculum = null)
{
    /// <summary>How long fetched u:find data is considered fresh.</summary>
    public TimeSpan UfindRefresh { get; init; } = TimeSpan.FromHours(6);
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");
    IReadOnlyList<ExamRegistrationDto> _regs = [];

    public async Task<UnivieSyncResult> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        List<EnrolmentDto> fresh;
        try { fresh = await uspace.GetEnrolmentsAsync(headless, ct); }
        catch (LoginRequiredException e) { log?.Invoke("u:space login required: " + e.Message); return new(0, 0, 0, e.Message, true); }
        catch (Exception e) when (e is HttpRequestException or UniDashboard.Core.Web.WebSecurityException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { log?.Invoke("u:space failed: " + e.Message); return new(0, 0, 0, e.Message); }

        _regs = uspace.LastExamRegistrations;
        foreach (var reg in _regs)
        {
            // Unknown JSON shape until the user has registered for an exam: remember each distinct shape once and log it.
            using var d = JsonDocument.Parse(reg.RawJson);
            var shape = UspaceClient.ShapeOf(d.RootElement);
            if (db.RememberExamShape(shape, reg.RawJson, now)) log?.Invoke("u:space exam registration shape: " + shape);
        }

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

        // Curriculum sections (STEOP, modules…) with their own exams, for the student's own studies.
        try { var (c, n) = await SyncSectionsAsync(uspace.LastStudies, initial, now, ct); changed += c; notified += n; }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        { log?.Invoke("section exams failed: " + e.Message); }

        notified += SendExamReminders(now);

        // Enrolments u:space no longer lists. Guard: an empty list may be an API hiccup.
        if (fresh.Count > 0)
        {
            var seen = fresh.Select(f => f.Key).ToHashSet();
            foreach (var gone in db.GetEnrolments().Where(e => !seen.Contains(e.Key)))
            {
                gone.Active = false; gone.UnseenChange = true; gone.LastSeenAt = now;
                db.SaveEnrolment(gone, null, Snapshot(gone, db.GetSessions(gone.Key), db.GetExams(gone.Key)), "NoLongerEnrolled");
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
        if (old?.UfindFetchedAt is not { } at || now - at > UfindRefresh || string.IsNullOrEmpty(old.UfindJson) || !old.UfindJson.Contains("\"Exams\"") || !old.UfindJson.Contains("\"Chapters\""))
        {
            try
            {
                var course = await ufind.GetCourseAsync(dto.LvNr, dto.Semester, ct);
                if (course is not null)
                {
                    rec.UfindFetchedAt = now;
                    if (course.Version != old?.UfindVersion || string.IsNullOrEmpty(old?.UfindJson) || !old.UfindJson.Contains("\"Exams\"") || !old.UfindJson.Contains("\"Chapters\""))
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

        // Exams: possible dates + registration windows from u:find, "registered" from u:space. Identity = u:find extid.
        var examMarker = $"exams_synced_{dto.Key}";
        var firstExams = old is null || db.GetSetting(examMarker) is null;
        var oldExams = db.GetExams(dto.Key);
        var (exams, examEvents) = ExamDiffer.Diff(oldExams, BuildExams(rec, _regs), now, firstExams);
        var regChanged = !oldExams.Where(e => e.Registered).Select(e => e.Key).OrderBy(k => k)
            .SequenceEqual(exams.Where(e => e.Registered).Select(e => e.Key).OrderBy(k => k));
        var saveExams = firstExams || examEvents.Count > 0 || regChanged;

        // Rows stored before exams were parsed gain an "Exams" block once: that is a migration, not a change.
        var migrating = old is not null && old.UfindJson.Length > 0 && (!old.UfindJson.Contains("\"Exams\"") || !old.UfindJson.Contains("\"Chapters\""));
        var infoChanged = old is not null && !migrating && old.ContentHash != rec.ContentHash;
        var statusChanged = old is not null && (old.Status != rec.Status || old.WaitlistPosition != rec.WaitlistPosition || !old.Active);
        var anyChange = old is null || infoChanged || events.Count > 0 || examEvents.Count > 0 || regChanged;

        rec.UnseenChange = (old?.UnseenChange ?? false) || (old is not null && (infoChanged || events.Count > 0 || examEvents.Count > 0));

        if (!anyChange)
        {
            db.SaveEnrolment(rec, null, null, null, saveExams ? exams : null);
            if (saveExams) db.SetSetting(examMarker, "1");
            return (0, 0);
        }

        var kinds = new List<string>();
        if (old is null) kinds.Add("New");
        if (statusChanged) kinds.Add("Status");
        if (infoChanged && !statusChanged) kinds.Add("Info");
        kinds.AddRange(events.GroupBy(e => e.Change).Select(g => $"{g.Key}×{g.Count()}"));
        kinds.AddRange(examEvents.GroupBy(e => e.Change).Select(g => $"Exam{g.Key}×{g.Count()}"));
        if (regChanged) kinds.Add("ExamRegistration");
        db.SaveEnrolment(rec, sessions, Snapshot(rec, sessions, exams), string.Join(", ", kinds), saveExams ? exams : null);
        if (saveExams) db.SetSetting(examMarker, "1");

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
            if (!firstExams) sent += NotifyExams(rec, examEvents, now);
        }
        return (1, sent);
    }

    // ---------- exams

    List<ExamRecord> BuildExams(EnrolmentRecord rec, IReadOnlyList<ExamRegistrationDto> regs)
    {
        var list = new List<ExamRecord>();
        UfindCourse? uf = string.IsNullOrEmpty(rec.UfindJson) ? null : JsonSerializer.Deserialize<UfindCourse>(rec.UfindJson);
        var g = uf?.Groups.FirstOrDefault(x => x.GroupId == $"{rec.LvNr}-{rec.GroupNr}") ?? uf?.Groups.FirstOrDefault();
        var mine = regs.Where(r => r.LvNr == rec.LvNr && (r.Semester.Length == 0 || r.Semester == rec.Semester)
                                   && !r.Status.Contains("ABGEMELDET", StringComparison.OrdinalIgnoreCase)).ToList();
        var used = new HashSet<ExamRegistrationDto>();

        foreach (var x in g?.Exams ?? [])
        {
            var reg = mine.FirstOrDefault(r => r.ExtId.Length > 0 && (r.ExtId == x.ExtId || r.ExtId == x.Id))
                      ?? mine.FirstOrDefault(r => r.Begin is { } b && b.ToLocalTime().Date == x.Begin.ToLocalTime().Date && !used.Contains(r));
            if (reg is not null) used.Add(reg);
            list.Add(new()
            {
                Key = $"{rec.Key}|{x.ExtId}", EnrolmentKey = rec.Key, ExtId = x.ExtId, Title = x.TitleDe.Length > 0 ? x.TitleDe : rec.Title,
                Begin = x.Begin, End = x.End, TimeKnown = x.TimeKnown, When = x.When, Room = reg?.Room,
                EnrollFrom = x.EnrollFrom, EnrollTo = x.EnrollTo, DisenrollUntil = x.DisenrollUntil, Registered = reg is not null,
            });
        }
        // Registered for a date u:find does not list (yet): still show it, from u:space data alone.
        foreach (var reg in mine.Where(r => !used.Contains(r) && r.Begin is not null))
        {
            var b = reg.Begin!.Value;
            list.Add(new()
            {
                Key = $"{rec.Key}|uspace:{(reg.ExtId.Length > 0 ? reg.ExtId : b.ToUnixTimeSeconds().ToString())}", EnrolmentKey = rec.Key,
                ExtId = reg.ExtId, Title = rec.Title, Begin = b, End = reg.End ?? b, TimeKnown = reg.End > b, Room = reg.Room, Registered = true,
            });
        }
        return list;
    }

    int NotifyExams(EnrolmentRecord rec, List<ExamEvent> events, DateTimeOffset now)
    {
        if (events.Count == 0 || !settings.NotifyCourse(CourseKinds.ExamChanged)) return 0;
        var list = events.Where(e => e.Exam.Begin > now.AddDays(-1)).ToList();   // past exam dates are not worth a toast
        if (list.Count == 0) return 0;
        string D(ExamRecord x) => x.TimeKnown ? x.Begin.ToLocalTime().ToString("ddd d.M.yyyy HH:mm", De) : x.Begin.ToLocalTime().ToString("ddd d.M.yyyy", De);
        var lines = list.Take(3).Select(e => e.Change switch
        {
            ExamChange.Added => $"New exam date {D(e.Exam)}",
            ExamChange.Changed => e.Previous!.Begin != e.Exam.Begin ? $"Exam date changed: {D(e.Previous)} → {D(e.Exam)}" : $"Exam {D(e.Exam)}: registration window changed",
            _ => $"Exam date removed: {D(e.Exam)}",
        });
        var body = string.Join("; ", lines) + (list.Count > 3 ? $" (+{list.Count - 3} more)" : "");
        return Send(new(Id(rec.Key), CourseKinds.ExamChanged, Hash(string.Join("|", list.Select(e => e.Change + e.Exam.Key + e.Exam.Hash))), rec.Title, body, rec.Key), now);
    }

    int SendExamReminders(DateTimeOffset now)
    {
        var n = 0;
        foreach (var p in NotificationRules.ExamReminders(db.GetAllExams(), settings, now)) n += Send(p, now);
        return n;
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

    public static string Snapshot(EnrolmentRecord r, IEnumerable<SessionRecord> sessions, IEnumerable<ExamRecord>? exams = null) => JsonSerializer.Serialize(new
    {
        r.Title, r.Type, r.Status, r.Semester, r.WaitlistPosition, r.StudyName, r.MoodleUrl, r.Remark, r.Active,
        Lecturers = JsonSerializer.Deserialize<List<LecturerDto>>(r.LecturersJson) ?? [],
        Ufind = string.IsNullOrEmpty(r.UfindJson) ? null : JsonSerializer.Deserialize<UfindCourse>(r.UfindJson),
        Exams = (exams ?? []).Select(x => new
        {
            Begin = x.Begin.ToUnixTimeSeconds(), End = x.End.ToUnixTimeSeconds(), x.TimeKnown, x.Registered, Status = x.Status.ToString(),
            EnrollFrom = x.EnrollFrom?.ToUnixTimeSeconds(), EnrollTo = x.EnrollTo?.ToUnixTimeSeconds(), DisenrollUntil = x.DisenrollUntil?.ToUnixTimeSeconds(),
        }),
        Sessions = sessions.Select(s => new
        {
            Begin = s.Begin.ToUnixTimeSeconds(), End = s.End.ToUnixTimeSeconds(), s.Room, Status = s.Status.ToString(), Change = s.Change.ToString(),
            PrevBegin = s.PrevBegin?.ToUnixTimeSeconds(), s.PrevRoom,
        }),
    });
}
