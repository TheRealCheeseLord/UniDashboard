using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniDashboard.Core;
using UniDashboard.Core.Calendar;
using UniDashboard.Core.Planning;
using UniDashboard.Core.Univie;

namespace UniDashboard.App;

public sealed partial class PlannerPage : Page
{
    static App A => App.Current;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");
    const double GapSliderMax = 61;     // the last stop means "Maximum"
    /// <summary>Optional tab requested via "--planner=calendar".</summary>
    public static string? RequestedTab { get; set; }
    bool _loading = true;

    List<PlanCourse> _courses = [];
    PlanResult? _result;
    readonly Dictionary<string, PlanEntry> _entryByEventId = new();

    public PlannerPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            foreach (RadioButton rb in StrategyButtons.Items)
                if ((string)rb.Tag == A.Settings.PlannerStrategy.ToString()) StrategyButtons.SelectedItem = rb;
            GapSlider.Value = A.Settings.PlannerTargetGap is { } g ? Math.Min(g, 60) : GapSliderMax;
            NotBeforePicker.Date = A.Settings.PlannerNotBefore is { } nb ? new DateTimeOffset(nb.ToDateTime(TimeOnly.MinValue)) : null;
            NotAfterPicker.Date = A.Settings.PlannerNotAfter is { } na ? new DateTimeOffset(na.ToDateTime(TimeOnly.MinValue)) : null;
            _loading = false;
            if (RequestedTab == "calendar") PageBar.SelectedItem = PageBar.Items[1];
            RequestedTab = null;
            Load();
            Refresh();
        };
    }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    void Load()
    {
        _courses = A.Db.GetAllExams().GroupBy(x => x.Enrolment.Key)
            .Select(g => new PlanCourse(g.Key, g.First().Enrolment.Title, g.Select(x => x.Exam).ToList()))
            .Where(c => c.Options.Any(o => o.Status == ExamStatus.Active))
            .OrderBy(c => c.Title).ToList();
    }

    PlanSettings CurrentSettings() => new()
    {
        Strategy = A.Settings.PlannerStrategy,
        Excluded = A.Settings.PlannerExcluded,
        Pins = A.Settings.PlannerPins,
        NotBefore = A.Settings.PlannerNotBefore,
        NotAfter = A.Settings.PlannerNotAfter,
        TargetGapDays = A.Settings.PlannerTargetGap,
    };

    static string Describe(ExamRecord x) => x.TimeKnown
        ? $"{x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  {x.Begin.ToLocalTime():HH:mm}–{x.End.ToLocalTime():HH:mm}"
        : $"{x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  (time not published yet)";

    // ---------- controls

    void Strategy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StrategyButtons.SelectedItem is not RadioButton rb) return;
        A.Settings.PlannerStrategy = Enum.Parse<PlanStrategy>((string)rb.Tag);
        Refresh();
    }

    void GapSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var v = (int)Math.Round(GapSlider.Value);
        GapLabel.Text = v >= GapSliderMax ? "as large as possible" : v == 1 ? "1 day" : $"{v} days";
        if (_loading) return;
        A.Settings.PlannerTargetGap = v >= GapSliderMax ? null : v;
        Refresh();
    }

    void Range_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_loading) return;
        DateOnly? From(CalendarDatePicker p) => p.Date is { } d ? DateOnly.FromDateTime(d.Date) : null;
        A.Settings.PlannerNotBefore = From(NotBeforePicker);
        A.Settings.PlannerNotAfter = From(NotAfterPicker);
        Refresh();
    }

    void ClearBefore_Click(object s, RoutedEventArgs e) => NotBeforePicker.Date = null;
    void ClearAfter_Click(object s, RoutedEventArgs e) => NotAfterPicker.Date = null;

    void ResetButton_Click(object s, RoutedEventArgs e) { A.Settings.PlannerPins = new(); Refresh(); }

    void PageBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var calendar = (PageBar.SelectedItem?.Tag as string) == "calendar";
        PlanScroll.Visibility = calendar ? Visibility.Collapsed : Visibility.Visible;
        CalendarScroll.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- plan

    void Refresh()
    {
        if (_loading) return;
        var now = DateTimeOffset.Now;
        var settings = CurrentSettings();
        var busy = A.Db.GetSessionsBetween(now.AddDays(-1), now.AddYears(2))
            .Where(x => x.Session.Status == SessionStatus.Active)
            .Select(x => new PlanBusy(x.Session.Begin, x.Session.End, x.Enrolment.Title)).ToList();

        _result = ExamPlanner.Plan(_courses, busy, settings, now);
        SpreadPanel.Visibility = settings.Strategy == PlanStrategy.Spread ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = settings.Pins.Count > 0;
        ClearBefore.IsEnabled = settings.NotBefore is not null;
        ClearAfter.IsEnabled = settings.NotAfter is not null;

        BuildPlan(_result, settings);
        BuildCalendar(_result);
    }

    void BuildPlan(PlanResult r, PlanSettings settings)
    {
        PlanPanel.Children.Clear();

        // ---- summary sentence
        var clashes = r.Entries.Count(e => e.Warnings.Any(w => w.StartsWith("⚠")));
        var unplanned = r.Entries.Count(e => e.UnplannedReason is not null);
        var summary = new List<string>();
        if (r.Timeline.Count > 0)
        {
            summary.Add(r.Timeline.Count == 1 ? "1 exam" : $"{r.Timeline.Count} exams");
            if (r.TightestGap is { } tg) summary.Add($"smallest gap {tg} {(tg == 1 ? "day" : "days")}");
            summary.Add(clashes == 0 ? "no clashes" : $"{clashes} with a clash");
            if (r.TargetGapDays is { } t && r.TargetMet is { } met)
                summary.Add(met ? $"gaps match the ideal of {t} days" : $"closest possible to the ideal gap of {t} days");
        }
        if (unplanned > 0) summary.Add($"{unplanned} course(s) not planned");
        PlanPanel.Children.Add(new TextBlock
        {
            Text = r.Timeline.Count == 0 && unplanned == 0 ? (_courses.Count == 0 ? "None of your courses has published exam dates yet." : "No course selected.") : string.Join("  ·  ", summary),
            FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });

        if (r.Timeline.Count > 1) PlanPanel.Children.Add(GapLegend(r.TargetGapDays));

        // ---- chronological cards with gap connectors
        for (var i = 0; i < r.Timeline.Count; i++)
        {
            if (i > 0) PlanPanel.Children.Add(Connector(r.Gaps[i - 1], settings, r.TargetGapDays));
            PlanPanel.Children.Add(PlanCard(r.Timeline[i]));
        }

        // ---- courses (include / pin / alternatives)
        PlanPanel.Children.Add(new TextBlock { Text = "Courses & dates", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 18, 0, 2) });
        foreach (var course in _courses)
            PlanPanel.Children.Add(Card(course, r.Entries.FirstOrDefault(e => e.Course.Key == course.Key), settings));
    }

    FrameworkElement Connector(int gap, PlanSettings settings, int? target)
    {
        var text = gap switch { 0 => "same day", 1 => "next day", _ => $"{gap} days later" };
        var color = GapBrush(gap, target);

        var g = new Grid { Margin = new Thickness(46, 0, 0, 0), ColumnSpacing = 10, MinHeight = 26 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Border { Width = 3, Background = color, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) });
        var tb = new TextBlock { Text = "▼  " + text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = color };
        Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        return g;
    }

    /// <summary>
    /// One colour rule for gaps (see the legend): same day = red, next day = amber (always); other gaps: with an ideal gap set
    /// green when close to it, amber when tighter, grey when wider; without one amber up to 3 days, grey beyond.
    /// </summary>
    static Brush GapBrush(int gap, int? target)
    {
        if (gap <= 0) return Res("SystemFillColorCriticalBrush");
        if (gap == 1) return Res("SystemFillColorCautionBrush");
        if (target is { } t)
        {
            var tol = Math.Max(1, (int)Math.Ceiling(t / 4.0));
            return Math.Abs(gap - t) <= tol ? Res("SystemFillColorSuccessBrush") : gap < t ? Res("SystemFillColorCautionBrush") : Res("TextFillColorSecondaryBrush");
        }
        return gap <= 3 ? Res("SystemFillColorCautionBrush") : Res("TextFillColorSecondaryBrush");
    }

    FrameworkElement GapLegend(int? target)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 2) };
        void Item(Brush b, string text) => sp.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            Children = { new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(2), Background = b }, new TextBlock { Text = text, FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") } },
        });
        sp.Children.Add(new TextBlock { Text = "Gap colours:", FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") });
        Item(Res("SystemFillColorCriticalBrush"), "same day");
        Item(Res("SystemFillColorCautionBrush"), target is null ? "next day / up to 3 days" : "next day / tighter than ideal");
        if (target is not null) Item(Res("SystemFillColorSuccessBrush"), "about the ideal gap");
        Item(Res("TextFillColorSecondaryBrush"), target is null ? "4+ days" : "more space than ideal");
        return sp;
    }

    FrameworkElement PlanCard(PlanEntry e)
    {
        var x = e.Chosen!;
        var local = x.Begin.ToLocalTime();
        var date = new StackPanel { Width = 74, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        date.Children.Add(new TextBlock { Text = local.ToString("ddd", De).TrimEnd('.').ToUpperInvariant(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Res("AccentTextFillColorPrimaryBrush") });
        date.Children.Add(new TextBlock { Text = local.Day.ToString(), FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        date.Children.Add(new TextBlock { Text = local.ToString("MMM yyyy", De), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") });

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = e.Course.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var badges = new List<string>();
        if (e.FixedReason == "registered") badges.Add("✅ registered");
        if (e.FixedReason == "pinned") badges.Add("📌 pinned");
        info.Children.Add(new TextBlock
        {
            Text = (x.TimeKnown ? $"{local:HH:mm} – {x.End.ToLocalTime():HH:mm}" : "time not published yet — whole day possible") + (badges.Count > 0 ? "     " + string.Join("  ", badges) : ""),
            Foreground = Res("TextFillColorSecondaryBrush"),
        });
        foreach (var w in e.Warnings) info.Children.Add(Warning(w, 0));

        var link = new HyperlinkButton { Content = "Open in calendar", Padding = new Thickness(0), FontSize = 12 };
        link.Click += (_, _) => A.Window?.HandleCommandLine($"--calendar=week@{local:yyyy-MM-dd}");
        info.Children.Add(link);

        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(date);
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        var hasClash = e.Warnings.Any(w => w.StartsWith("⚠"));
        return new Border
        {
            Child = g, Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(8),
            Background = Res("LayerFillColorDefaultBrush"),
            BorderBrush = hasClash ? Res("SystemFillColorCriticalBrush") : e.FixedReason == "registered" ? Res("AccentFillColorDefaultBrush") : Res("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(hasClash || e.FixedReason == "registered" ? 2 : 1),
        };
    }

    // ---------- courses & alternatives

    FrameworkElement Card(PlanCourse course, PlanEntry? entry, PlanSettings settings)
    {
        var included = entry is not null;
        var stack = new StackPanel { Spacing = 4 };

        var include = new CheckBox { IsChecked = included, Content = course.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 0 };
        include.Click += (_, _) =>
        {
            var ex = A.Settings.PlannerExcluded;
            if (include.IsChecked == true) ex.Remove(course.Key); else ex.Add(course.Key);
            A.Settings.PlannerExcluded = ex;
            Refresh();
        };
        stack.Children.Add(include);

        if (entry is not null)
        {
            if (entry.Chosen is { } c)
                stack.Children.Add(new TextBlock
                {
                    Text = "Recommended: " + Describe(c), FontSize = 14, Margin = new Thickness(28, 0, 0, 0), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                });
            else
                stack.Children.Add(new TextBlock { Text = "Not planned: " + entry.UnplannedReason, Margin = new Thickness(28, 0, 0, 0), Foreground = Res("SystemFillColorCautionBrush") });

            if (entry.Options.Count > 0 && entry.FixedReason != "registered")
                stack.Children.Add(Alternatives(entry, settings));
        }
        else
            stack.Children.Add(new TextBlock { Text = "Excluded from the plan", Margin = new Thickness(28, 0, 0, 0), Opacity = 0.6 });

        return new Border
        {
            Child = stack, Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(8),
            Background = Res("LayerFillColorDefaultBrush"), BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            Opacity = included ? 1 : 0.75,
        };
    }

    TextBlock Warning(string w, double left = 28) => new()
    {
        Text = w, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, 0, 0, 0),
        Foreground = w.StartsWith("⚠") ? Res("SystemFillColorCriticalBrush")
            : w.StartsWith("⏰") || w.StartsWith("Caution") || w.StartsWith("⏳") ? Res("SystemFillColorCautionBrush")
            : Res("TextFillColorSecondaryBrush"),
    };

    FrameworkElement Alternatives(PlanEntry entry, PlanSettings settings)
    {
        var inner = new StackPanel { Spacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        settings.Pins.TryGetValue(entry.Course.Key, out var pinned);

        foreach (var o in entry.Options)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1 };
            var isChosen = entry.Chosen?.Key == o.Exam.Key;
            text.Children.Add(new TextBlock
            {
                Text = (isChosen ? "▶ " : "   ") + Describe(o.Exam), TextWrapping = TextWrapping.Wrap,
                FontWeight = isChosen ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = o.Eligible ? 1 : 0.55,
            });
            if (entry.AlternativeWarnings.TryGetValue(o.Exam.Key, out var ws))
                foreach (var w in ws) text.Children.Add(Warning(w));
            row.Children.Add(text);

            if (o.Eligible || pinned == o.Exam.Key)
            {
                var isPinned = pinned == o.Exam.Key;
                var pin = new Button { Content = isPinned ? "Unpin" : "Pin this date", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 3, 10, 3) };
                pin.Click += (_, _) =>
                {
                    var pins = A.Settings.PlannerPins;
                    if (isPinned) pins.Remove(entry.Course.Key); else pins[entry.Course.Key] = o.Exam.Key;
                    A.Settings.PlannerPins = pins;
                    Refresh();
                };
                Grid.SetColumn(pin, 1);
                row.Children.Add(pin);
            }
            inner.Children.Add(row);
        }

        return new Expander
        {
            Header = $"All dates ({entry.Options.Count})", Content = inner, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 4, 0, 0),
        };
    }

    // ---------- miniature calendar: exams in colour, everything else as grey blocks without text

    const double PxPerHour = 16, GutterW = 38;

    void BuildCalendar(PlanResult r)
    {
        CalendarPanel.Children.Clear();
        _entryByEventId.Clear();
        if (r.Timeline.Count == 0)
        {
            CalendarPanel.Children.Add(new TextBlock { Text = "Nothing planned yet.", Opacity = 0.7 });
            return;
        }

        var examWeeks = r.Timeline.Select(e => CalendarModel.StartOfWeek(CalendarModel.DayOf(e.Chosen!.Begin))).Distinct().OrderBy(d => d).ToList();
        var weeks = new List<DateOnly>(examWeeks);
        if (!weeks.Contains(examWeeks[0].AddDays(-7))) weeks.Insert(0, examWeeks[0].AddDays(-7));   // one week of context before the first exam

        CalendarPanel.Children.Add(Legend());

        var from = StartOf(weeks[0]);
        var to = StartOf(weeks[^1].AddDays(7));
        var others = LoadOthers(from, to);

        for (var i = 0; i < weeks.Count; i++)
        {
            if (i > 0)
            {
                var skipped = (weeks[i].DayNumber - weeks[i - 1].DayNumber) / 7 - 1;
                if (skipped > 0)
                    CalendarPanel.Children.Add(new TextBlock
                    {
                        Text = $"— {skipped} week{(skipped == 1 ? "" : "s")} without exams —", HorizontalAlignment = HorizontalAlignment.Center,
                        FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 4, 0, 4),
                    });
            }
            CalendarPanel.Children.Add(WeekStrip(weeks[i], r.Timeline, others));
        }
    }

    FrameworkElement Legend()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        FrameworkElement Item(Brush b, string text) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = b }, new TextBlock { Text = text, FontSize = 12 } },
        };
        sp.Children.Add(Item(Res("AccentFillColorDefaultBrush"), "Recommended exam"));
        sp.Children.Add(Item(new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x80, 0x80, 0x80)), "Other events (lectures etc.)"));
        sp.Children.Add(Item(new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x6A, 0x5A, 0xCD)), "Exam day, time not published"));
        return sp;
    }

    static DateTimeOffset StartOf(DateOnly d)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    List<CalendarEvent> LoadOthers(DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<CalendarEvent>();
        foreach (var (s, _) in A.Db.GetSessionsBetween(from, to).Where(x => x.Session.Status == SessionStatus.Active && x.Session.End >= from && x.Session.Begin < to))
            list.Add(new($"o:{s.Key}", "", s.Begin, s.End, false));
        foreach (var e in A.Db.GetEvents(from, to).Where(e => !e.AllDay))
            list.Add(new($"o:{e.Id}", "", e.Start, e.End, false));
        if (A.Settings.ShowDemoEvents)
            foreach (var e in CalendarModel.DemoEvents(DateOnly.FromDateTime(from.LocalDateTime)).Where(e => !e.AllDay && e.End >= from && e.Start < to))
                list.Add(new($"o:{e.Id}", "", e.Start, e.End, false));
        return list;
    }

    FrameworkElement WeekStrip(DateOnly monday, List<PlanEntry> timeline, List<CalendarEvent> others)
    {
        var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var examsThisWeek = timeline.Where(e => days.Contains(CalendarModel.DayOf(e.Chosen!.Begin))).ToList();

        // Visible hours: 08–22, widened when an exam falls outside.
        double lo = 8, hi = 22;
        foreach (var e in examsThisWeek.Where(e => e.Chosen!.TimeKnown))
        {
            lo = Math.Min(lo, Math.Floor(e.Chosen!.Begin.ToLocalTime().TimeOfDay.TotalHours));
            hi = Math.Max(hi, Math.Ceiling(e.Chosen.End.ToLocalTime().TimeOfDay.TotalHours));
        }
        var height = (hi - lo) * PxPerHour;

        // Events per day: exams (coloured) + others (grey), packed side by side when they overlap.
        var perDay = new Dictionary<DateOnly, List<CalendarEvent>>();
        foreach (var d in days) perDay[d] = others.Where(o => CalendarModel.DayOf(o.Start) == d).ToList();
        foreach (var e in examsThisWeek.Where(e => e.Chosen!.TimeKnown))
        {
            var x = e.Chosen!;
            var id = $"plan:{x.Key}";
            _entryByEventId[id] = e;
            perDay[CalendarModel.DayOf(x.Begin)].Add(new(id, e.Course.Title, x.Begin, x.End, false, Source: "plan"));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(GutterW) });
        for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(height) });

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < 7; i++)
        {
            var d = days[i];
            var head = new TextBlock
            {
                Text = $"{d.ToString("ddd", De).TrimEnd('.')} {d.Day}.{d.Month}.", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 3),
                FontWeight = examsThisWeek.Any(e => CalendarModel.DayOf(e.Chosen!.Begin) == d) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = d == today ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(head, 1 + i);
            grid.Children.Add(head);

            var canvas = new Canvas { Background = Res("LayerFillColorDefaultBrush") };
            var date = d;
            canvas.SizeChanged += (_, ev) => DrawMiniDay(canvas, date, perDay[date], timeline, lo, hi, ev.NewSize.Width);
            Grid.SetColumn(canvas, 1 + i); Grid.SetRow(canvas, 1);
            grid.Children.Add(canvas);
            var line = new Border { BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
            Grid.SetColumn(line, 1 + i); Grid.SetRow(line, 1);
            grid.Children.Add(line);
        }

        var gutter = new Canvas();
        for (var h = (int)lo + 1; h < hi; h += 2)
        {
            var t = new TextBlock { Text = $"{h:00}", FontSize = 9, Foreground = Res("TextFillColorSecondaryBrush") };
            Canvas.SetLeft(t, 12); Canvas.SetTop(t, (h - lo) * PxPerHour - 7);
            gutter.Children.Add(t);
        }
        Grid.SetColumn(gutter, 0); Grid.SetRow(gutter, 1);
        grid.Children.Add(gutter);

        return new Border
        {
            Child = grid, BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 0, 4, 4), Background = Res("LayerFillColorDefaultBrush"),
        };
    }

    void DrawMiniDay(Canvas canvas, DateOnly day, List<CalendarEvent> events, List<PlanEntry> timeline, double lo, double hi, double width)
    {
        canvas.Children.Clear();
        if (width <= 0) return;
        canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, (hi - lo) * PxPerHour) };

        for (var h = (int)lo; h < hi; h++)
        {
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = 1, Fill = Res("DividerStrokeColorDefaultBrush"), Opacity = 0.6, IsHitTestVisible = false };
            Canvas.SetTop(line, (h - lo) * PxPerHour);
            canvas.Children.Add(line);
        }

        // Exam day without a published time: a light band over the whole day.
        foreach (var e in timeline.Where(e => !e.Chosen!.TimeKnown && CalendarModel.DayOf(e.Chosen.Begin) == day))
        {
            var band = new Button
            {
                Width = width, Height = (hi - lo) * PxPerHour, Padding = new Thickness(3, 2, 3, 2), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x6A, 0x5A, 0xCD)),
                Content = new TextBlock { Text = e.Course.Title + " (time TBA)", FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            var entry = e;
            band.Click += (_, _) => ShowExamFlyout(band, entry);
            canvas.Children.Add(band);
        }

        foreach (var b in CalendarModel.LayoutTimed(day, events, minDurationMin: 25))
        {
            var startH = Math.Max(lo, b.StartMin / 60);
            var endH = Math.Min(hi, b.EndMin / 60);
            if (endH <= startH) continue;
            var w = (width - 4) / b.Columns;
            var top = (startH - lo) * PxPerHour;
            var h = Math.Max(6, (endH - startH) * PxPerHour - 1);

            if (b.Event.Source == "plan" && _entryByEventId.TryGetValue(b.Event.Id, out var entry))
            {
                var x = entry.Chosen!;
                var btn = new Button
                {
                    Width = Math.Max(8, w - 1), Height = h, Padding = new Thickness(3, 1, 3, 1), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
                    Background = Res("AccentFillColorDefaultBrush"),
                    VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = new TextBlock
                    {
                        Text = (entry.FixedReason == "registered" ? "✅ " : entry.FixedReason == "pinned" ? "📌 " : "") + entry.Course.Title + (h > 28 ? $"\n{x.Begin.ToLocalTime():HH:mm}" : ""),
                        FontSize = 10, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextOnAccentFillColorPrimaryBrush"),
                    },
                };
                btn.Click += (_, _) => ShowExamFlyout(btn, entry);
                Canvas.SetLeft(btn, 2 + b.Column * w); Canvas.SetTop(btn, top);
                canvas.Children.Add(btn);
            }
            else
            {
                // Other events: anonymous grey block, no text.
                var rect = new Border
                {
                    Width = Math.Max(6, w - 2), Height = h, CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x80, 0x80, 0x80)),
                };
                Canvas.SetLeft(rect, 2 + b.Column * w); Canvas.SetTop(rect, top);
                canvas.Children.Add(rect);
            }
        }
    }

    void ShowExamFlyout(FrameworkElement anchor, PlanEntry e)
    {
        var x = e.Chosen!;
        var sp = new StackPanel { Spacing = 5, MinWidth = 240, MaxWidth = 360 };
        sp.Children.Add(new TextBlock { Text = e.Course.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = Describe(x), TextWrapping = TextWrapping.Wrap });
        foreach (var w in e.Warnings) sp.Children.Add(Warning(w, 0));
        var link = new HyperlinkButton { Content = "Open in calendar", Padding = new Thickness(0) };
        var fly = new Flyout { Content = sp };
        link.Click += (_, _) => { fly.Hide(); A.Window?.HandleCommandLine($"--calendar=week@{x.Begin.ToLocalTime():yyyy-MM-dd}"); };
        sp.Children.Add(link);
        fly.ShowAt(anchor);
    }
}
