using System.Text.Json;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Univie;

/// <summary>
/// Section exams: curriculum sections (STEOP I/II, modules, study sections) have their own exams that are not part of any course.
/// For each of the student's studies (u:space) the matching u:find curriculum is picked (<see cref="CurriculumMatcher"/>), its study plan
/// tree is fetched, and the exams of every section that contains an enrolled course become exams of a synthetic enrolment
/// (<see cref="EnrolmentRecord.IsSection"/>), so calendar, planner, export and reminders need no special handling.
/// </summary>
public partial class UnivieSyncService
{
    /// <summary>How long a fetched study plan tree is considered fresh.</summary>
    public TimeSpan TreeRefresh { get; init; } = TimeSpan.FromHours(6);

    async Task<(int Changed, int Notified)> SyncSectionsAsync(IReadOnlyList<StudyDto> studies, bool initial, DateTimeOffset now, CancellationToken ct)
    {
        if (curriculum is null || studies.Count == 0) return (0, 0);

        int changed = 0, notified = 0;
        var seen = new HashSet<string>();
        var semesters = new HashSet<string>();
        var gotTree = false;

        // Only the two newest semesters: older section exams are history and would just cost requests.
        foreach (var bySem in db.GetEnrolments().GroupBy(e => e.Semester).OrderByDescending(g => g.Key).Take(2))
        {
            var sem = bySem.Key;
            var lvs = bySem.Select(e => e.LvNr).ToHashSet();
            var chapters = bySem.SelectMany(e => ChaptersOf(e)).ToList();
            foreach (var (study, cur) in CurriculumMatcher.Select(studies, chapters))
            {
                ct.ThrowIfCancellationRequested();
                var tree = await GetTreeAsync(cur, sem, now, ct);
                if (tree is null) continue;
                gotTree = true; semesters.Add(sem);

                foreach (var node in tree.Flatten().Skip(1).Where(n => n.Exams.Count > 0 && n.Flatten().SelectMany(x => x.Courses).Any(c => lvs.Contains(c.LvNr))))
                {
                    try
                    {
                        var (c, n) = await ProcessSectionAsync(node, sem, study, initial, now, ct);
                        changed += c; notified += n;
                        seen.Add(EnrolmentRecord.SectionKey(sem, node.PathId));
                    }
                    catch (HttpRequestException e) { log?.Invoke($"section {node.PathId}/{sem} failed: {e.Message}"); seen.Add(EnrolmentRecord.SectionKey(sem, node.PathId)); }
                }
            }
        }

        // Sections that no longer apply (study ended, enrolments gone): deactivate. Only when this run really saw the plans.
        if (gotTree)
            foreach (var old in db.GetEnrolments(includeSections: true).Where(e => e.IsSection && semesters.Contains(e.Semester) && !seen.Contains(e.Key)))
                db.SetEnrolmentActive(old.Key, false);

        return (changed, notified);
    }

    static IEnumerable<UfindChapter> ChaptersOf(EnrolmentRecord e)
    {
        if (string.IsNullOrEmpty(e.UfindJson)) return [];
        try { return JsonSerializer.Deserialize<UfindCourse>(e.UfindJson)?.Chapters ?? []; }
        catch (JsonException) { return []; }
    }

    async Task<CurriculumNode?> GetTreeAsync(CurriculumCandidate cur, string sem, DateTimeOffset now, CancellationToken ct)
    {
        var cached = db.GetCurriculumTree(cur.RootPath, sem);
        CurriculumNode? stored = null;
        if (cached is { } c0)
            try { stored = JsonSerializer.Deserialize<CurriculumNode>(c0.Json); } catch (JsonException) { }
        if (stored is not null && stored.Schema >= CurriculumClient.CurrentSchema && now - cached!.Value.FetchedAt <= TreeRefresh) return stored;
        try
        {
            var tree = await curriculum!.GetTreeAsync(cur.RootPath, sem, ct);
            if (tree is null) return stored;
            if (string.IsNullOrWhiteSpace(tree.Title)) tree.Title = cur.Name;
            db.SaveCurriculumTree(cur.RootPath, sem, cur.Name, JsonSerializer.Serialize(tree), now);
            return tree;
        }
        catch (HttpRequestException e) { log?.Invoke($"study plan {cur.RootPath}/{sem} failed: {e.Message}"); return stored; }
    }

    async Task<(int Changed, int Notified)> ProcessSectionAsync(CurriculumNode node, string sem, StudyDto study, bool initial, DateTimeOffset now, CancellationToken ct)
    {
        var key = EnrolmentRecord.SectionKey(sem, node.PathId);
        var old = db.GetEnrolment(key);
        var rec = new EnrolmentRecord
        {
            Key = key, Semester = sem, Title = node.Title, Type = "", Status = "SECTION", StudyName = study.Name, Active = true, LastSeenAt = now,
            UfindLink = $"{CurriculumClient.BaseUrl}/vvz_sub.html?semester={sem}&path={node.PathId}", ContentHash = Hash(node.Title),
            UnseenChange = old?.UnseenChange ?? false,
        };

        var dates = new List<UfindExam>();
        foreach (var x in node.Exams)
        {
            var ex = await curriculum!.GetExamAsync(x.ModPath, x.Eq, sem, ct);
            if (ex is not null) dates.AddRange(ex.Dates.Select(d => d with { TitleDe = ex.Title.Length > 0 ? ex.Title : x.Title }));
        }

        var fresh = new List<ExamRecord>();
        var mine = _regs.Where(r => r.LvNr.Length == 0 && (r.Semester.Length == 0 || r.Semester == sem)
                                    && !r.Status.Contains("ABGEMELDET", StringComparison.OrdinalIgnoreCase)).ToList();
        var used = new HashSet<ExamRegistrationDto>();
        foreach (var d in dates)
        {
            var reg = mine.FirstOrDefault(r => r.ExtId.Length > 0 && (r.ExtId == d.Id || r.ExtId == d.ExtId))
                      ?? mine.FirstOrDefault(r => !used.Contains(r) && r.Begin is { } b && b.ToLocalTime().Date == d.Begin.ToLocalTime().Date);
            if (reg is not null) used.Add(reg);
            fresh.Add(new()
            {
                Key = $"{key}|{d.ExtId}", EnrolmentKey = key, ExtId = d.ExtId, Title = d.TitleDe, Begin = d.Begin, End = d.End, TimeKnown = d.TimeKnown,
                Room = d.When, Registered = reg is not null,
            });
        }

        var marker = $"exams_synced_{key}";
        var first = old is null || db.GetSetting(marker) is null;
        var oldExams = db.GetExams(key);
        // Nothing fetched (page missing): keep what we have rather than marking every date removed.
        if (dates.Count == 0 && oldExams.Count > 0)
        {
            db.SaveEnrolment(rec, null, null, null);
            return (0, 0);
        }
        var (exams, events) = ExamDiffer.Diff(oldExams, fresh, now, first);
        var regChanged = !oldExams.Where(e => e.Registered).Select(e => e.Key).OrderBy(k => k)
            .SequenceEqual(exams.Where(e => e.Registered).Select(e => e.Key).OrderBy(k => k));
        var save = first || events.Count > 0 || regChanged;
        rec.UnseenChange = rec.UnseenChange || (old is not null && events.Count > 0);

        if (events.Count == 0 && !regChanged)
        {
            db.SaveEnrolment(rec, null, null, null, save ? exams : null);
            if (save) db.SetSetting(marker, "1");
            return (0, 0);
        }

        var kinds = events.GroupBy(e => e.Change).Select(g => $"Exam{g.Key}×{g.Count()}").ToList();
        if (regChanged) kinds.Add("ExamRegistration");
        db.SaveEnrolment(rec, null, Snapshot(rec, [], exams), string.Join(", ", kinds), exams);
        db.SetSetting(marker, "1");
        var sent = !initial && !first ? NotifyExams(rec, events, now) : 0;
        return (1, sent);
    }
}
