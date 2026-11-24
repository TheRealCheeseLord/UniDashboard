using UniDashboard.Core.Univie;

namespace UniDashboard.Core.Calendar;

public enum MetaKind { Assignment, Event, ExamWindow, Exam }

/// <summary>One chip in the sticky meta row. Spans StartDay..EndDay (inclusive) so ranges need no new rendering code.</summary>
public record MetaItem(
    MetaKind Kind, string Title, DateOnly StartDay, DateOnly EndDay,
    long AssignmentId = 0, Assignment? Assignment = null, CalendarEvent? Event = null,
    bool Submitted = false, bool Overdue = false,
    ExamRecord? Exam = null, string? CourseTitle = null, bool Registered = false, string State = "");

/// <summary>A meta item placed on the visible columns: 0-based column, column span and lane (row).</summary>
public record MetaPlacement(MetaItem Item, int Col, int Span, int Lane);

/// <summary>A timed event placed inside a day column (minutes since midnight; Column/Columns for overlap packing).</summary>
public record TimedBlock(CalendarEvent Event, double StartMin, double EndMin, int Column, int Columns);

public static class CalendarModel
{
    public static DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(t.ToLocalTime().DateTime);

    /// <summary>The last day an event touches (an end exactly at midnight belongs to the previous day).</summary>
    public static DateOnly LastDay(CalendarEvent e) =>
        DayOf(e.End > e.Start ? e.End.AddTicks(-1) : e.Start);

    /// <summary>All-day and multi-day events live in the meta row; the rest in the time grid.</summary>
    public static bool IsMeta(CalendarEvent e) => e.AllDay || LastDay(e) > DayOf(e.Start);

    public static DateOnly StartOfWeek(DateOnly d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7;   // Monday first
        return d.AddDays(-diff);
    }

    /// <summary>Items visible in [first, first+dayCount) laid out in lanes (greedy, longest spans first).</summary>
    public static List<MetaPlacement> BuildMetaRow(
        DateOnly first, int dayCount, IEnumerable<Assignment> assignments, IEnumerable<CalendarEvent> events, DateTimeOffset now,
        IEnumerable<(ExamRecord Exam, EnrolmentRecord Enrolment)>? exams = null)
    {
        var last = first.AddDays(dayCount - 1);
        var items = new List<MetaItem>();

        foreach (var a in assignments)
        {
            if (a.DueAt is not { } due) continue;
            var day = DayOf(due);
            if (day < first || day > last) continue;
            items.Add(new(MetaKind.Assignment, a.Name, day, day, a.Id, a, null, a.Submitted, !a.Submitted && due < now));
        }
        foreach (var e in events.Where(IsMeta))
        {
            var s = DayOf(e.Start);
            var l = LastDay(e);
            if (l < first || s > last) continue;
            items.Add(new(MetaKind.Event, e.Title, s, l, 0, null, e));
        }

        AddExamItems(items, first, last, exams, now);

        var ordered = items
            .OrderByDescending(i => i.EndDay.DayNumber - i.StartDay.DayNumber)
            .ThenBy(i => i.StartDay).ThenBy(i => i.Kind).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var laneEnds = new List<int>();   // last occupied column per lane
        var placed = new List<MetaPlacement>();
        foreach (var i in ordered)
        {
            var c0 = Math.Max(0, i.StartDay.DayNumber - first.DayNumber);
            var c1 = Math.Min(dayCount - 1, i.EndDay.DayNumber - first.DayNumber);
            var lane = laneEnds.FindIndex(end => end < c0);
            if (lane < 0) { laneEnds.Add(c1); lane = laneEnds.Count - 1; } else laneEnds[lane] = c1;
            placed.Add(new(i, c0, c1 - c0 + 1, lane));
        }
        return placed.OrderBy(p => p.Lane).ThenBy(p => p.Col).ToList();
    }

    /// <summary>
    /// Exams with a known time are events in the time grid (see the calendar page); date-only exams (time not published)
    /// become all-day style chips on their day; a published registration window becomes a range bar over its days.
    /// </summary>
    static void AddExamItems(List<MetaItem> items, DateOnly first, DateOnly last,
        IEnumerable<(ExamRecord Exam, EnrolmentRecord Enrolment)>? exams, DateTimeOffset now)
    {
        if (exams is null) return;
        var list = exams.ToList();
        var registeredCourses = list.Where(x => x.Exam.Registered && x.Exam.Status == ExamStatus.Active).Select(x => x.Enrolment.Key).ToHashSet();

        foreach (var (x, e) in list)
        {
            var removed = x.Status == ExamStatus.Removed;
            var recent = x.Begin > now.AddDays(-7);
            var state = removed ? "removed" : x.Change == ExamChange.Changed && recent ? "changed" : "";

            if (!x.TimeKnown)
            {
                var d = DayOf(x.Begin);
                if (d >= first && d <= last)
                    items.Add(new(MetaKind.Exam,
                        (x.Registered ? "✅ " : "") + "Exam: " + e.Title + (x.TimeKnown ? " " + x.Begin.ToLocalTime().ToString("HH:mm") : ""),
                        d, d, Exam: x, CourseTitle: e.Title, Registered: x.Registered, State: state));
            }

            // Registration bar: only when published, not for past/removed exam dates.
            if (x.HasWindow && !removed && x.Begin > now.AddDays(-1))
            {
                var s = DayOf(x.EnrollFrom!.Value);
                var l = DayOf(x.EnrollTo!.Value);
                if (l >= first && s <= last)
                    items.Add(new(MetaKind.ExamWindow, $"Register: {e.Title} (exam {DayOf(x.Begin):d.M.})", s, l,
                        Exam: x, CourseTitle: e.Title, Registered: registeredCourses.Contains(e.Key), State: state));
            }
        }
    }

    /// <summary>Timed (single-day) events of one day with side-by-side column packing for overlaps.</summary>
    public static List<TimedBlock> LayoutTimed(DateOnly day, IEnumerable<CalendarEvent> events, double minDurationMin = 20)
    {
        var items = events
            .Where(e => !IsMeta(e) && DayOf(e.Start) == day)
            .Select(e =>
            {
                var s = e.Start.ToLocalTime().TimeOfDay.TotalMinutes;
                var en = Math.Max(s + minDurationMin, e.End.ToLocalTime().TimeOfDay.TotalMinutes);
                return (E: e, S: s, En: Math.Min(en, 24 * 60));
            })
            .OrderBy(x => x.S).ThenByDescending(x => x.En).ToList();

        var result = new List<TimedBlock>();
        var cluster = new List<(CalendarEvent E, double S, double En, int Col)>();
        double clusterEnd = double.MinValue;

        void Flush()
        {
            if (cluster.Count == 0) return;
            var cols = cluster.Max(c => c.Col) + 1;
            result.AddRange(cluster.Select(c => new TimedBlock(c.E, c.S, c.En, c.Col, cols)));
            cluster.Clear();
        }

        foreach (var x in items)
        {
            if (x.S >= clusterEnd) { Flush(); clusterEnd = double.MinValue; }
            var col = 0;
            while (cluster.Any(c => c.Col == col && c.En > x.S)) col++;
            cluster.Add((x.E, x.S, x.En, col));
            clusterEnd = Math.Max(clusterEnd, x.En);
        }
        Flush();
        return result;
    }

    /// <summary>Fake events around the given week for reviewing the UI. Deterministic.</summary>
    public static List<CalendarEvent> DemoEvents(DateOnly today)
    {
        var monday = StartOfWeek(today);
        DateTimeOffset At(int dayOffset, int h, int m = 0) =>
            new(monday.AddDays(dayOffset).ToDateTime(new TimeOnly(h, m)), TimeZoneInfo.Local.GetUtcOffset(monday.AddDays(dayOffset).ToDateTime(new TimeOnly(h, m))));

        return
        [
            new("demo-1", "Analysis I — Lecture", At(0, 9), At(0, 10, 30), false, "HS 1, Oskar-Morgenstern-Platz 1", "Weekly lecture", null, "demo", "Analysis I"),
            new("demo-2", "Algebra — Tutorial", At(0, 10), At(0, 11, 30), false, "SR 3", null, null, "demo", "Algebra"),
            new("demo-3", "Study group", At(1, 14), At(1, 16), false, "Library, 2nd floor", "Bring exercise sheet 4", "https://example.org", "demo"),
            new("demo-4", "Seminar", At(2, 12), At(2, 13, 30), false, "Online", null, "https://example.org/meeting", "demo", "Seminar"),
            new("demo-5", "Seminar prep", At(2, 13), At(2, 14), false, null, null, null, "demo"),
            new("demo-6", "Exam period", At(3, 0), At(5, 0), true, null, "Multi-day range example", null, "demo"),
            new("demo-7", "Public holiday", At(4, 0), At(5, 0), true, null, null, null, "demo"),
            new("demo-8", "Lab session", At(4, 8), At(4, 11), false, "Lab 2", null, null, "demo", "Physics"),
        ];
    }
}
