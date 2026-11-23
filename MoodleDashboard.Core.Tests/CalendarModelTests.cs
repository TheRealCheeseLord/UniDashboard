using MoodleDashboard.Core;
using MoodleDashboard.Core.Calendar;

namespace MoodleDashboard.Core.Tests;

public class CalendarModelTests
{
    static readonly DateOnly Monday = new(2026, 11, 16);
    static DateTimeOffset L(int dayOffset, int h, int m = 0)
    {
        var dt = Monday.AddDays(dayOffset).ToDateTime(new TimeOnly(h, m));
        return new(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }
    static CalendarEvent Ev(string id, DateTimeOffset s, DateTimeOffset e, bool allDay = false) => new(id, id, s, e, allDay);
    static Assignment As(long id, DateTimeOffset due, bool submitted = false) => new() { Id = id, Name = "A" + id, DueAt = due, Submitted = submitted };

    [Fact]
    public void Week_starts_on_monday()
    {
        Assert.Equal(Monday, CalendarModel.StartOfWeek(new DateOnly(2026, 11, 22)));  // Sunday
        Assert.Equal(Monday, CalendarModel.StartOfWeek(Monday));
    }

    [Fact]
    public void Assignment_lands_on_its_local_due_day_and_flags_overdue()
    {
        var now = L(2, 12);
        var meta = CalendarModel.BuildMetaRow(Monday, 7, [As(1, L(2, 23, 30)), As(2, L(1, 8)), As(3, L(1, 9), submitted: true)], [], now);
        Assert.Equal(2, meta.Single(p => p.Item.AssignmentId == 1).Col);
        Assert.False(meta.Single(p => p.Item.AssignmentId == 1).Item.Overdue);
        Assert.True(meta.Single(p => p.Item.AssignmentId == 2).Item.Overdue);
        Assert.False(meta.Single(p => p.Item.AssignmentId == 3).Item.Overdue);
    }

    [Fact]
    public void Assignment_outside_the_visible_range_is_excluded()
    {
        Assert.Empty(CalendarModel.BuildMetaRow(Monday, 7, [As(1, L(7, 10)), As(2, L(-1, 10))], [], L(0, 0)));
    }

    [Fact]
    public void Multi_day_event_spans_columns_and_clips_to_the_week()
    {
        var e = Ev("r", L(5, 0), L(9, 0), allDay: true);   // Sat..Tue next week
        var p = CalendarModel.BuildMetaRow(Monday, 7, [], [e], L(0, 0)).Single();
        Assert.Equal(5, p.Col);
        Assert.Equal(2, p.Span);
    }

    [Fact]
    public void All_day_event_ending_at_midnight_does_not_spill_into_the_next_day()
    {
        var e = Ev("h", L(4, 0), L(5, 0), allDay: true);
        var p = CalendarModel.BuildMetaRow(Monday, 7, [], [e], L(0, 0)).Single();
        Assert.Equal((4, 1), (p.Col, p.Span));
    }

    [Fact]
    public void Overlapping_meta_items_get_separate_lanes()
    {
        var items = CalendarModel.BuildMetaRow(Monday, 7, [As(1, L(2, 10))], [Ev("r", L(1, 0), L(4, 0), true)], L(0, 0));
        Assert.Equal(2, items.Select(i => i.Lane).Distinct().Count());
    }

    [Fact]
    public void Timed_events_are_not_in_the_meta_row_but_in_the_grid()
    {
        var e = Ev("t", L(0, 9), L(0, 10));
        Assert.Empty(CalendarModel.BuildMetaRow(Monday, 7, [], [e], L(0, 0)));
        var b = CalendarModel.LayoutTimed(Monday, [e]).Single();
        Assert.Equal(9 * 60, b.StartMin);
        Assert.Equal(10 * 60, b.EndMin);
    }

    [Fact]
    public void Overlapping_timed_events_are_packed_side_by_side()
    {
        var blocks = CalendarModel.LayoutTimed(Monday, [Ev("a", L(0, 9), L(0, 11)), Ev("b", L(0, 10), L(0, 12)), Ev("c", L(0, 13), L(0, 14))]);
        var a = blocks.Single(x => x.Event.Id == "a");
        var b = blocks.Single(x => x.Event.Id == "b");
        var c = blocks.Single(x => x.Event.Id == "c");
        Assert.NotEqual(a.Column, b.Column);
        Assert.Equal(2, a.Columns);
        Assert.Equal(1, c.Columns);
    }

    [Fact]
    public void Events_of_other_days_are_not_laid_out()
    {
        Assert.Empty(CalendarModel.LayoutTimed(Monday, [Ev("x", L(1, 9), L(1, 10))]));
    }

    [Fact]
    public void Database_roundtrips_events_by_overlap()
    {
        var db = new Database(Path.GetTempFileName());
        db.UpsertEvents([Ev("1", L(0, 9), L(0, 10)), Ev("2", L(10, 9), L(10, 10))]);
        var got = db.GetEvents(L(0, 0), L(7, 0));
        Assert.Equal("1", got.Single().Id);
    }

    [Fact]
    public void Demo_events_are_deterministic_and_inside_the_week()
    {
        var a = CalendarModel.DemoEvents(new DateOnly(2026, 11, 19));
        Assert.Equal(a, CalendarModel.DemoEvents(new DateOnly(2026, 11, 19)));
        Assert.All(a, e => Assert.InRange(CalendarModel.DayOf(e.Start), Monday, Monday.AddDays(6)));
    }
}
