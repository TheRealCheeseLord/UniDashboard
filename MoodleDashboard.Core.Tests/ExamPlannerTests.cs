using System.Diagnostics;
using MoodleDashboard.Core.Planning;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.Core.Tests;

public class ExamPlannerTests
{
    static DateTimeOffset At(int month, int day, int h = 10) => new(new DateTime(2099, month, day, h, 0, 0, DateTimeKind.Local));
    static readonly DateTimeOffset Now = At(1, 1, 8);

    static ExamRecord Ex(string course, string id, DateTimeOffset begin, bool timeKnown = true, DateTimeOffset? from = null, DateTimeOffset? to = null,
        bool registered = false, ExamStatus status = ExamStatus.Active) => new()
    {
        Key = $"{course}|{id}", EnrolmentKey = course, ExtId = id, Title = course, Begin = begin,
        End = timeKnown ? begin.AddHours(2) : begin, TimeKnown = timeKnown, EnrollFrom = from, EnrollTo = to, Registered = registered, Status = status,
    };

    static PlanCourse C(string key, params ExamRecord[] options) => new(key, key, options);
    static PlanSettings S(PlanStrategy s, IEnumerable<string>? excluded = null, Dictionary<string, string>? pins = null) =>
        new() { Strategy = s, Excluded = new HashSet<string>(excluded ?? []), Pins = pins ?? new() };
    static PlanBusy Busy(DateTimeOffset b, int hours = 2) => new(b, b.AddHours(hours), "Lecture");

    static DateTimeOffset Chosen(PlanResult r, string course) => r.Entries.Single(e => e.Course.Key == course).Chosen!.Begin;

    static readonly PlanCourse A = C("A", Ex("A", "a1", At(2, 10)), Ex("A", "a2", At(2, 20)));
    static readonly PlanCourse B = C("B", Ex("B", "b1", At(2, 11)), Ex("B", "b2", At(2, 25)));

    [Fact]
    public void Earliest_picks_the_first_date_of_every_course()
    {
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Equal(At(2, 11), Chosen(r, "B"));
    }

    [Fact]
    public void Latest_picks_the_last_date_of_every_course()
    {
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Latest), Now);
        Assert.Equal(At(2, 20), Chosen(r, "A"));
        Assert.Equal(At(2, 25), Chosen(r, "B"));
    }

    [Fact]
    public void Spread_maximises_the_smallest_gap()
    {
        // A10+B25 has a gap of 15 days; every other combination is closer.
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Spread), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Equal(At(2, 25), Chosen(r, "B"));
        Assert.Equal([15], r.Gaps);
    }

    [Fact]
    public void Spread_with_three_courses_uses_leximin()
    {
        var c = C("C", Ex("C", "c1", At(2, 17)), Ex("C", "c2", At(3, 5)));
        var r = ExamPlanner.Plan([A, B, c], [], S(PlanStrategy.Spread), Now);
        // best smallest gap: A10, C17(+7)... compare by brute force
        var best = new[] { 10, 20 }.SelectMany(a => new[] { 11, 25 }.Select(b => (a, b))).SelectMany(ab => new[] { 17, 33 }.Select(cDay => new[] { ab.a, ab.b, cDay }))
            .Select(days => days.OrderBy(d => d).ToArray())
            .Select(d => new[] { d[1] - d[0], d[2] - d[1] }.OrderBy(x => x).ToArray())
            .OrderByDescending(g => g[0]).ThenByDescending(g => g[1]).First();
        Assert.Equal(best, r.Gaps.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_clash_with_a_session_is_avoided_when_another_date_exists()
    {
        var course = C("X", Ex("X", "x1", At(2, 10, 10)), Ex("X", "x2", At(2, 20, 10)));
        var r = ExamPlanner.Plan([course], [Busy(At(2, 10, 11))], S(PlanStrategy.Earliest), Now);
        Assert.Equal(At(2, 20, 10), Chosen(r, "X"));            // earliest would clash
        Assert.Contains(r.Entries[0].AlternativeWarnings["X|x1"], w => w.Contains("Clashes"));
    }

    [Fact]
    public void An_unavoidable_clash_is_allowed_but_flagged()
    {
        var course = C("X", Ex("X", "x1", At(2, 10, 10)));
        var r = ExamPlanner.Plan([course], [Busy(At(2, 10, 11))], S(PlanStrategy.Earliest), Now);
        var e = r.Entries[0];
        Assert.NotNull(e.Chosen);
        Assert.Contains(e.Warnings, w => w.Contains("Clashes"));
        Assert.True(r.TotalPenalty >= 1000);
    }

    [Fact]
    public void Date_only_exam_is_whole_day_with_caution_not_a_hard_clash()
    {
        var course = C("X", Ex("X", "x1", At(2, 10, 0), timeKnown: false));
        var r = ExamPlanner.Plan([course], [Busy(At(2, 10, 9))], S(PlanStrategy.Earliest), Now);
        var e = r.Entries[0];
        Assert.NotNull(e.Chosen);
        Assert.Contains(e.Warnings, w => w.Contains("whole day"));
        Assert.Contains(e.Warnings, w => w.StartsWith("Caution"));
        Assert.DoesNotContain(e.Warnings, w => w.Contains("Clashes"));
        Assert.True(r.TotalPenalty < 100);
    }

    [Fact]
    public void Two_exams_on_the_same_day_with_one_date_only_get_a_caution_and_are_avoided_if_possible()
    {
        var dateOnly = C("D", Ex("D", "d1", At(2, 10, 0), timeKnown: false));
        var timed = C("T", Ex("T", "t1", At(2, 10, 10)), Ex("T", "t2", At(2, 14, 10)));
        var r = ExamPlanner.Plan([dateOnly, timed], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(At(2, 14, 10), Chosen(r, "T"));
    }

    [Fact]
    public void Overlapping_timed_exams_are_avoided()
    {
        var a = C("A", Ex("A", "a1", At(2, 10, 10)));
        var b = C("B", Ex("B", "b1", At(2, 10, 11)), Ex("B", "b2", At(2, 12, 10)));
        var r = ExamPlanner.Plan([a, b], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(At(2, 12, 10), Chosen(r, "B"));
    }

    [Fact]
    public void Dates_with_a_closed_registration_window_are_never_recommended()
    {
        var course = C("X", Ex("X", "x1", At(2, 10), from: At(1, 1, 0).AddDays(-30), to: Now.AddDays(-1)), Ex("X", "x2", At(2, 20)));
        var r = ExamPlanner.Plan([course], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(At(2, 20), Chosen(r, "X"));
        Assert.False(r.Entries[0].Options.First().Eligible);
    }

    [Fact]
    public void Unpublished_or_not_yet_open_windows_are_allowed_with_a_warning_only()
    {
        var course = C("X", Ex("X", "x1", At(2, 10)), Ex("X", "x2", At(3, 10), from: At(2, 1), to: At(2, 20)));
        var early = ExamPlanner.Plan([course], [], S(PlanStrategy.Earliest), Now);
        Assert.Contains(early.Entries[0].Warnings, w => w.Contains("not published"));
        var late = ExamPlanner.Plan([course], [], S(PlanStrategy.Latest), Now);
        Assert.Equal(At(3, 10), Chosen(late, "X"));
        Assert.Contains(late.Entries[0].Warnings, w => w.StartsWith("Registration opens"));
    }

    [Fact]
    public void Registered_exam_is_fixed_and_others_are_planned_around_it()
    {
        var a = C("A", Ex("A", "a1", At(2, 10)), Ex("A", "a2", At(2, 20), registered: true));
        var b = C("B", Ex("B", "b1", At(2, 19)), Ex("B", "b2", At(3, 15)));
        var r = ExamPlanner.Plan([a, b], [], S(PlanStrategy.Spread), Now);
        Assert.Equal(At(2, 20), Chosen(r, "A"));
        Assert.Equal("registered", r.Entries.Single(e => e.Course.Key == "A").FixedReason);
        Assert.Equal(At(3, 15), Chosen(r, "B"));
    }

    [Fact]
    public void Pinned_date_is_respected_and_others_planned_around_it()
    {
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Earliest, pins: new() { ["A"] = "A|a2" }), Now);
        Assert.Equal(At(2, 20), Chosen(r, "A"));
        Assert.Equal("pinned", r.Entries.Single(e => e.Course.Key == "A").FixedReason);
        Assert.Equal(At(2, 11), Chosen(r, "B"));
    }

    [Fact]
    public void Excluded_courses_are_ignored()
    {
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Earliest, excluded: ["B"]), Now);
        Assert.DoesNotContain(r.Entries, e => e.Course.Key == "B");
    }

    [Fact]
    public void Course_without_eligible_dates_is_reported_unplanned_without_blocking_others()
    {
        var closed = C("Z", Ex("Z", "z1", At(2, 10), from: At(1, 1, 0).AddDays(-30), to: Now.AddDays(-2)));
        var none = C("N");
        var r = ExamPlanner.Plan([A, closed, none], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal("All registration windows have closed", r.Entries.Single(e => e.Course.Key == "Z").UnplannedReason);
        Assert.Equal("No exam dates published yet", r.Entries.Single(e => e.Course.Key == "N").UnplannedReason);
        Assert.NotNull(r.Entries.Single(e => e.Course.Key == "A").Chosen);
    }

    [Fact]
    public void Past_dates_are_not_eligible_but_date_only_today_is()
    {
        var past = Ex("X", "x1", At(1, 1, 7));      // before Now (08:00)
        var todayDateOnly = Ex("X", "x2", At(1, 1, 0), timeKnown: false);
        var r = ExamPlanner.Plan([C("X", past, todayDateOnly)], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(todayDateOnly.Key, r.Entries[0].Chosen!.Key);
    }

    [Fact]
    public void Strategy_does_not_override_clash_avoidance()
    {
        var a = C("A", Ex("A", "a1", At(2, 10, 10)), Ex("A", "a2", At(2, 20, 10)));
        var busy = new[] { Busy(At(2, 20, 11)) };
        var r = ExamPlanner.Plan([a], busy, S(PlanStrategy.Latest), Now);
        Assert.Equal(At(2, 10, 10), Chosen(r, "A"));     // "latest" would clash
    }

    [Fact]
    public void Large_instance_finishes_quickly_and_matches_the_per_course_optimum_without_conflicts()
    {
        var courses = Enumerable.Range(0, 12).Select(i =>
            C($"C{i}", Enumerable.Range(0, 6).Select(j => Ex($"C{i}", $"e{j}", At(2, 1 + j * 4 + i % 3, 8 + i))).ToArray())).ToList();
        var sw = Stopwatch.StartNew();
        var r = ExamPlanner.Plan(courses, [], S(PlanStrategy.Earliest), Now);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.All(r.Entries, e => Assert.NotNull(e.Chosen));
        Assert.Equal(0, r.TotalPenalty);                 // there is always a conflict-free assignment here
        Assert.All(r.Entries, e => Assert.Equal(e.Course.Options.Min(o => o.Begin).Date, e.Chosen!.Begin.Date));
    }

    static PlanSettings Range(PlanStrategy st, DateOnly? from = null, DateOnly? to = null, int? target = null, Dictionary<string, string>? pins = null) =>
        new() { Strategy = st, NotBefore = from, NotAfter = to, TargetGapDays = target, Pins = pins ?? new() };

    [Fact]
    public void Not_before_excludes_earlier_dates_for_every_strategy()
    {
        foreach (var st in new[] { PlanStrategy.Earliest, PlanStrategy.Latest, PlanStrategy.Spread })
        {
            var r = ExamPlanner.Plan([A, B], [], Range(st, from: new DateOnly(2099, 2, 15)), Now);
            Assert.Equal(At(2, 20), Chosen(r, "A"));
            Assert.Equal(At(2, 25), Chosen(r, "B"));
        }
    }

    [Fact]
    public void Not_after_excludes_later_dates()
    {
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Latest, to: new DateOnly(2099, 2, 12)), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Equal(At(2, 11), Chosen(r, "B"));
        Assert.Equal("Outside your date range", r.Entries[0].Options.Last().IneligibleReason);
    }

    [Fact]
    public void Range_is_inclusive_on_both_ends()
    {
        var r = ExamPlanner.Plan([A], [], Range(PlanStrategy.Earliest, from: new DateOnly(2099, 2, 10), to: new DateOnly(2099, 2, 10)), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
    }

    [Fact]
    public void Course_without_any_date_in_range_is_unplanned_with_a_clear_reason()
    {
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Earliest, from: new DateOnly(2099, 3, 1)), Now);
        Assert.All(r.Entries, e => Assert.Equal("No exam date within your date range", e.UnplannedReason));
    }

    [Fact]
    public void Pinned_date_outside_the_range_is_kept_with_a_warning()
    {
        var r = ExamPlanner.Plan([A], [], Range(PlanStrategy.Earliest, from: new DateOnly(2099, 2, 15), pins: new() { ["A"] = "A|a1" }), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Contains(r.Entries[0].Warnings, w => w.Contains("outside your date range"));
    }

    [Fact]
    public void Target_gap_picks_the_earliest_arrangement_that_meets_it()
    {
        // 7-day target: A10+B25 (15 d) and A20+B11 (9 d) both qualify; A20+B11 is earlier overall.
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Spread, target: 7), Now);
        Assert.Equal(At(2, 20), Chosen(r, "A"));
        Assert.Equal(At(2, 11), Chosen(r, "B"));
        Assert.True(r.TargetMet);
        Assert.Equal(9, r.TightestGap);
    }

    [Fact]
    public void Small_target_does_not_push_dates_further_than_needed()
    {
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Spread, target: 1), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));          // earliest arrangement already has a 1-day gap
        Assert.Equal(At(2, 11), Chosen(r, "B"));
        Assert.True(r.TargetMet);
    }

    [Fact]
    public void Unreachable_target_falls_back_to_the_least_shortfall()
    {
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Spread, target: 60), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Equal(At(2, 25), Chosen(r, "B"));          // the widest arrangement
        Assert.False(r.TargetMet);
    }

    [Fact]
    public void No_target_keeps_the_maximum_spread_behaviour()
    {
        var withNull = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Spread, target: null), Now);
        var old = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Spread), Now);
        Assert.Equal(Chosen(old, "A"), Chosen(withNull, "A"));
        Assert.Equal(Chosen(old, "B"), Chosen(withNull, "B"));
        Assert.Null(withNull.TargetMet);
    }

    [Fact]
    public void Target_gap_is_ignored_by_the_other_strategies()
    {
        var r = ExamPlanner.Plan([A, B], [], Range(PlanStrategy.Earliest, target: 30), Now);
        Assert.Equal(At(2, 10), Chosen(r, "A"));
        Assert.Null(r.TargetGapDays);
    }

    [Fact]
    public void Timeline_is_sorted_and_gaps_are_in_days()
    {
        var r = ExamPlanner.Plan([A, B], [], S(PlanStrategy.Earliest), Now);
        Assert.Equal(["A", "B"], r.Timeline.Select(e => e.Course.Key).ToArray());
        Assert.Equal([1], r.Gaps);
    }
}
