using System.Globalization;
using MoodleDashboard.Core.Calendar;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.Core.Planning;

public enum PlanStrategy { Earliest, Latest, Spread }

public record PlanCourse(string Key, string Title, IReadOnlyList<ExamRecord> Options);
public record PlanBusy(DateTimeOffset Begin, DateTimeOffset End, string Title);

public class PlanSettings
{
    public PlanStrategy Strategy { get; init; } = PlanStrategy.Spread;
    public ISet<string> Excluded { get; init; } = new HashSet<string>();
    /// <summary>courseKey → ExamRecord.Key the user pinned.</summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();
    /// <summary>Optional date range (inclusive local days) applied to every strategy.</summary>
    public DateOnly? NotBefore { get; init; }
    public DateOnly? NotAfter { get; init; }
    /// <summary>"Spread" only: ideal gap between exams in days; null = as large as possible.</summary>
    public int? TargetGapDays { get; init; }
}

/// <summary>One possible date of a course with everything worth telling the user about it.</summary>
public class PlanOption
{
    public ExamRecord Exam { get; init; } = null!;
    public bool Eligible { get; set; } = true;
    public string? IneligibleReason { get; set; }
    public int SessionPenalty { get; set; }
    public List<string> Warnings { get; } = [];
    public DateTimeOffset From { get; init; }     // occupied interval: exact for timed exams, the whole local day otherwise
    public DateTimeOffset To { get; init; }
}

public class PlanEntry
{
    public PlanCourse Course { get; init; } = null!;
    public ExamRecord? Chosen { get; set; }
    public string FixedReason { get; set; } = "";   // "registered" | "pinned" | ""
    public string? UnplannedReason { get; set; }
    public List<string> Warnings { get; } = [];
    public List<PlanOption> Options { get; } = [];
    /// <summary>Warnings per option key when swapped into the current plan (clash with the other chosen exams).</summary>
    public Dictionary<string, List<string>> AlternativeWarnings { get; } = new();
}

public class PlanResult
{
    public List<PlanEntry> Entries { get; } = [];
    /// <summary>Chosen exams in date order.</summary>
    public List<PlanEntry> Timeline { get; } = [];
    /// <summary>Days between consecutive chosen exams (same length as Timeline − 1).</summary>
    public List<int> Gaps { get; } = [];
    public int TotalPenalty { get; set; }
    /// <summary>Spread with a target gap: was the target reached everywhere? (null when no target / fewer than two exams)</summary>
    public bool? TargetMet { get; set; }
    public int? TargetGapDays { get; set; }
    /// <summary>Smallest gap between consecutive chosen exams in days (null with fewer than two exams).</summary>
    public int? TightestGap => Gaps.Count > 0 ? Gaps.Min() : null;
}

/// <summary>
/// Recommends one exam date per course. Lexicographic objective: (1) as few clashes/cautions as possible — clashing dates are
/// allowed but penalised — then (2) the chosen strategy: earliest, latest, or most spaced out (maximise the smallest gap between
/// exams, then the next smallest, …). Never registers anything; purely advisory.
/// </summary>
public static class ExamPlanner
{
    const int Clash = 1000, SameDayDateOnly = 200, SessionDayDateOnly = 10;
    const long ExhaustiveLimit = 250_000;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    public static PlanResult Plan(IEnumerable<PlanCourse> courses, IReadOnlyList<PlanBusy> busy, PlanSettings settings, DateTimeOffset now)
    {
        var result = new PlanResult();
        var today = CalendarModel.DayOf(now);
        var candidates = new List<(PlanEntry Entry, List<PlanOption> Choices)>();

        foreach (var course in courses.Where(c => !settings.Excluded.Contains(c.Key)))
        {
            var entry = new PlanEntry { Course = course };
            foreach (var x in course.Options.Where(o => o.Status == ExamStatus.Active).OrderBy(o => o.Begin))
                entry.Options.Add(BuildOption(x, busy, now, today, settings));
            result.Entries.Add(entry);

            // Which options may the search choose from?
            List<PlanOption> choices;
            var registered = entry.Options.Where(o => o.Exam.Registered).ToList();
            if (registered.Count > 0)
            {
                choices = [registered[0]];
                entry.FixedReason = "registered";
            }
            else if (settings.Pins.TryGetValue(course.Key, out var pin) && entry.Options.FirstOrDefault(o => o.Exam.Key == pin) is { } pinned)
            {
                choices = [pinned];
                entry.FixedReason = "pinned";
            }
            else choices = entry.Options.Where(o => o.Eligible).ToList();

            if (choices.Count == 0)
            {
                entry.UnplannedReason = entry.Options.Count == 0 ? "No exam dates published yet"
                    : entry.Options.Any(o => o.IneligibleReason == "Outside your date range") ? "No exam date within your date range"
                    : entry.Options.All(o => o.IneligibleReason?.Contains("closed") == true) ? "All registration windows have closed"
                    : "No upcoming exam date available";
                continue;
            }
            candidates.Add((entry, choices));
        }

        if (candidates.Count > 0)
        {
            var picks = Search(candidates.Select(c => c.Choices).ToList(), settings.Strategy, today, settings.TargetGapDays);
            for (var i = 0; i < candidates.Count; i++) candidates[i].Entry.Chosen = picks[i].Exam;
            var chosenOptions = picks;

            // Warnings for the chosen dates
            for (var i = 0; i < candidates.Count; i++)
            {
                var e = candidates[i].Entry;
                if (!picks[i].Eligible && picks[i].IneligibleReason is { } why && e.FixedReason == "pinned") e.Warnings.Add("⚠ Pinned date: " + why.ToLowerInvariant());
                e.Warnings.AddRange(picks[i].Warnings);
                for (var j = 0; j < picks.Count; j++)
                    if (j != i) AddPairWarning(e.Warnings, picks[i], picks[j], candidates[j].Entry.Course.Title);
                e.Warnings.AddRange(RegistrationWarnings(picks[i].Exam, now));
            }

            // Warnings per alternative if it replaced the chosen date
            for (var i = 0; i < candidates.Count; i++)
            {
                var e = candidates[i].Entry;
                foreach (var o in e.Options)
                {
                    var w = new List<string>(o.Warnings);
                    for (var j = 0; j < picks.Count; j++)
                        if (j != i) AddPairWarning(w, o, picks[j], candidates[j].Entry.Course.Title);
                    w.AddRange(RegistrationWarnings(o.Exam, now));
                    if (!o.Eligible && o.IneligibleReason is not null) w.Insert(0, o.IneligibleReason);
                    e.AlternativeWarnings[o.Exam.Key] = w;
                }
            }

            result.TotalPenalty = Evaluate(chosenOptions, today, settings.TargetGapDays).Penalty;
        }

        foreach (var e in result.Entries.Where(e => e.Chosen is not null).OrderBy(e => e.Chosen!.Begin)) result.Timeline.Add(e);
        for (var i = 1; i < result.Timeline.Count; i++)
            result.Gaps.Add(CalendarModel.DayOf(result.Timeline[i].Chosen!.Begin).DayNumber - CalendarModel.DayOf(result.Timeline[i - 1].Chosen!.Begin).DayNumber);
        result.TargetGapDays = settings.Strategy == PlanStrategy.Spread ? settings.TargetGapDays : null;
        if (result.TargetGapDays is { } tg && result.Gaps.Count > 0) result.TargetMet = result.Gaps.All(g => g >= tg);
        return result;
    }

    // ---------- options

    static PlanOption BuildOption(ExamRecord x, IReadOnlyList<PlanBusy> busy, DateTimeOffset now, DateOnly today, PlanSettings settings)
    {
        var day = CalendarModel.DayOf(x.Begin);
        var from = x.TimeKnown ? x.Begin : StartOfDay(day);
        var to = x.TimeKnown ? x.End : StartOfDay(day.AddDays(1));
        var o = new PlanOption { Exam = x, From = from, To = to };

        // Eligibility: still ahead of us, and registration not already closed.
        if (x.TimeKnown ? x.End < now : day < today) { o.Eligible = false; o.IneligibleReason = "Date already passed"; }
        else if (x.HasWindow && x.EnrollTo < now) { o.Eligible = false; o.IneligibleReason = "Registration already closed"; }
        else if ((settings.NotBefore is { } nb && day < nb) || (settings.NotAfter is { } na && day > na))
        { o.Eligible = false; o.IneligibleReason = "Outside your date range"; }

        if (x.TimeKnown)
        {
            foreach (var b in busy.Where(b => b.Begin < to && b.End > from))
            {
                o.SessionPenalty += Clash;
                o.Warnings.Add($"⚠ Clashes with {b.Title} ({b.Begin.ToLocalTime().ToString("ddd d.M. HH:mm", De)}–{b.End.ToLocalTime():HH:mm})");
            }
        }
        else
        {
            o.Warnings.Add("⏳ Exam time not published yet — the whole day is assumed possible");
            var sameDay = busy.Where(b => CalendarModel.DayOf(b.Begin) == day).Select(b => b.Title).Distinct().ToList();
            if (sameDay.Count > 0)
            {
                o.SessionPenalty += SessionDayDateOnly;
                o.Warnings.Add("Caution: sessions that day: " + string.Join(", ", sameDay));
            }
        }
        return o;
    }

    static DateTimeOffset StartOfDay(DateOnly d)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    static IEnumerable<string> RegistrationWarnings(ExamRecord x, DateTimeOffset now)
    {
        if (x.Registered) { yield return "✅ Already registered for this date"; yield break; }
        string F(DateTimeOffset d) => d.ToLocalTime().ToString("ddd d.M. HH:mm", De);
        if (!x.HasWindow) yield return "Registration window not published yet";
        else if (now < x.EnrollFrom) yield return $"Registration opens {F(x.EnrollFrom!.Value)}";
        else if (now <= x.EnrollTo)
            yield return x.EnrollTo!.Value - now <= TimeSpan.FromDays(3) ? $"⏰ Registration closes soon ({F(x.EnrollTo.Value)})" : $"Registration open until {F(x.EnrollTo.Value)}";
        else yield return $"Registration closed ({F(x.EnrollTo!.Value)})";
    }

    // ---------- pairs

    static int PairPenalty(PlanOption a, PlanOption b)
    {
        var sameDay = CalendarModel.DayOf(a.Exam.Begin) == CalendarModel.DayOf(b.Exam.Begin);
        if (a.Exam.TimeKnown && b.Exam.TimeKnown) return a.From < b.To && b.From < a.To ? Clash : 0;
        return sameDay ? SameDayDateOnly : 0;
    }

    static void AddPairWarning(List<string> warnings, PlanOption mine, PlanOption other, string otherTitle)
    {
        var p = PairPenalty(mine, other);
        if (p >= Clash) warnings.Add($"⚠ Overlaps with the exam of {otherTitle}");
        else if (p > 0) warnings.Add($"Caution: same day as the exam of {otherTitle} (time not published)");
    }

    // ---------- scoring & search

    sealed record Score(int Penalty, double[] AscGaps, double SumDays, double Shortfall);

    static Score Evaluate(IReadOnlyList<PlanOption> picks, DateOnly today, int? target)
    {
        var penalty = picks.Sum(p => p.SessionPenalty);
        for (var i = 0; i < picks.Count; i++)
            for (var j = i + 1; j < picks.Count; j++) penalty += PairPenalty(picks[i], picks[j]);

        var days = picks.Select(p => (double)(CalendarModel.DayOf(p.Exam.Begin).DayNumber - today.DayNumber)).OrderBy(d => d).ToList();
        var gaps = new List<double>();
        for (var i = 1; i < days.Count; i++) gaps.Add(days[i] - days[i - 1]);
        gaps.Sort();
        var shortfall = target is { } t ? gaps.Sum(g => g < t ? (t - g) * (t - g) : 0) : 0;
        return new(penalty, gaps.ToArray(), days.Sum(), shortfall);
    }

    /// <summary>True when <paramref name="a"/> is strictly better than <paramref name="b"/>.</summary>
    static bool Better(Score a, Score b, PlanStrategy strategy, int? target)
    {
        if (a.Penalty != b.Penalty) return a.Penalty < b.Penalty;
        switch (strategy)
        {
            case PlanStrategy.Latest:
                return a.SumDays > b.SumDays;
            case PlanStrategy.Spread when target is not null:
                // Reach the ideal gap with as little squared shortfall as possible; once it is met, don't push dates later than needed.
                if (a.Shortfall != b.Shortfall) return a.Shortfall < b.Shortfall;
                if (a.Shortfall > 0)
                    for (var i = 0; i < Math.Min(a.AscGaps.Length, b.AscGaps.Length); i++)
                        if (a.AscGaps[i] != b.AscGaps[i]) return a.AscGaps[i] > b.AscGaps[i];
                return a.SumDays < b.SumDays;
            case PlanStrategy.Spread:
                for (var i = 0; i < Math.Min(a.AscGaps.Length, b.AscGaps.Length); i++)
                    if (a.AscGaps[i] != b.AscGaps[i]) return a.AscGaps[i] > b.AscGaps[i];
                return a.SumDays < b.SumDays;       // equal spacing: prefer earlier
            default:
                return a.SumDays < b.SumDays;
        }
    }

    static List<PlanOption> Search(List<List<PlanOption>> choices, PlanStrategy strategy, DateOnly today, int? target)
    {
        long product = 1;
        foreach (var c in choices) { product *= c.Count; if (product > ExhaustiveLimit) break; }

        List<PlanOption>? best = null;
        Score? bestScore = null;

        if (product <= ExhaustiveLimit)
        {
            var pick = new PlanOption[choices.Count];
            void Dfs(int i, int penaltySoFar)
            {
                if (bestScore is not null && penaltySoFar > bestScore.Penalty) return;   // penalty only grows
                if (i == choices.Count)
                {
                    var s = Evaluate(pick, today, target);
                    if (bestScore is null || Better(s, bestScore, strategy, target)) { bestScore = s; best = [.. pick]; }
                    return;
                }
                foreach (var o in choices[i])
                {
                    pick[i] = o;
                    var add = o.SessionPenalty;
                    for (var j = 0; j < i; j++) add += PairPenalty(pick[j], o);
                    Dfs(i + 1, penaltySoFar + add);
                }
            }
            Dfs(0, 0);
            return best!;
        }

        // Large instances: start from the individually best choice, then improve one course at a time.
        var current = choices.Select(c => c.OrderBy(o => o.SessionPenalty).ThenBy(o => strategy == PlanStrategy.Latest ? -o.Exam.Begin.ToUnixTimeSeconds() : o.Exam.Begin.ToUnixTimeSeconds()).First()).ToList();
        var score = Evaluate(current, today, target);
        for (var pass = 0; pass < 50; pass++)
        {
            var improved = false;
            for (var i = 0; i < choices.Count; i++)
                foreach (var o in choices[i])
                {
                    if (ReferenceEquals(o, current[i])) continue;
                    var trial = new List<PlanOption>(current) { [i] = o };
                    var s = Evaluate(trial, today, target);
                    if (Better(s, score, strategy, target)) { current = trial; score = s; improved = true; }
                }
            if (!improved) break;
        }
        return current;
    }
}
