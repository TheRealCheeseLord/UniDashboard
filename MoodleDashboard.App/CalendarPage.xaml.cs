using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Calendar;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.App;

public sealed partial class CalendarPage : Page
{
    enum View { Day, Week, Month }

    static App A => App.Current;
    static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-AT");
    const double GutterW = 56, HourH = 48;
    const int MaxLanes = 3;

    /// <summary>Optional view requested via "--calendar=day|week|month".</summary>
    public static string? RequestedView { get; set; }

    DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    View _view = View.Week;
    double _scroll = 7 * HourH;          // opens at 07:00
    ScrollViewer? _scroller;
    bool _scrollApplied;
    readonly DispatcherQueueTimer _tick;
    bool _ready;

    public CalendarPage()
    {
        InitializeComponent();
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromMinutes(1);
        _tick.Tick += (_, _) => Render();
        Loaded += (_, _) =>
        {
            _ready = true;
            if (RequestedView is { } rv && Enum.TryParse<View>(rv, true, out var v)) { _view = v; ViewBox.SelectedIndex = (int)v; }
            RequestedView = null;
            Render();
            _tick.Start();
        };
        Unloaded += (_, _) => _tick.Stop();
    }

    // ---------- helpers

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", Culture);
    static string DayLong(DateOnly d) => d.ToString("dddd, d. MMMM yyyy", Culture);
    static DateTimeOffset StartOf(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue)));

    static string EventWhen(CalendarEvent e)
    {
        var s = CalendarModel.DayOf(e.Start);
        var l = CalendarModel.LastDay(e);
        if (e.AllDay) return s == l ? $"{DayLong(s)} (all day)" : $"{s:d. MMM} – {l:d. MMM yyyy} (all day)";
        return s == l ? $"{DayLong(s)}, {Time(e.Start)} – {Time(e.End)}"
                      : $"{e.Start.ToLocalTime():d. MMM HH:mm} – {e.End.ToLocalTime():d. MMM HH:mm}";
    }

    List<CalendarEvent> LoadEvents(DateOnly first, int days)
    {
        var from = StartOf(first);
        var to = StartOf(first.AddDays(days));
        var list = A.Db.GetEvents(from, to);
        if (A.Settings.ShowDemoEvents)
            list.AddRange(CalendarModel.DemoEvents(_anchor).Where(e => e.End >= from && e.Start < to));
        list.AddRange(UnivieEvents(from, to));
        return list;
    }

    /// <summary>Course sessions (u:space) as calendar events, incl. moved ghosts and cancelled sessions.</summary>
    List<CalendarEvent> UnivieEvents(DateTimeOffset from, DateTimeOffset to)
    {
        var res = new List<CalendarEvent>();
        var now = DateTimeOffset.Now;
        var showGhosts = A.Settings.ShowMovedRemoved;
        string Fmt(DateTimeOffset t) => t.ToLocalTime().ToString("ddd d.M. HH:mm", Culture);

        foreach (var (s, e) in A.Db.GetSessionsBetween(from, to))
        {
            var recent = s.Begin > now.AddDays(-7);          // change labels only matter until shortly after the event
            var room = string.IsNullOrWhiteSpace(s.Room) ? null : s.Room;
            string id = $"uspace:{s.Key}";
            string desc = $"{e.Type} · {e.Semester}";

            if (s.Status == SessionStatus.Removed)
            {
                if (showGhosts && recent && s.End >= from && s.Begin < to)
                    res.Add(new(id, e.Title, s.Begin, s.End, false, room, desc, e.MoodleUrl, "uspace", e.Title, "removed", "Cancelled / removed"));
                continue;
            }

            var changed = recent && s.Change is SessionChange.Moved or SessionChange.Changed && s.PrevBegin is not null;
            if (s.End >= from && s.Begin < to)
            {
                var note = !changed ? null
                    : s.Change == SessionChange.Moved ? $"Moved from {Fmt(s.PrevBegin!.Value)}"
                    : $"Changed (was {Fmt(s.PrevBegin!.Value)}" + (s.PrevRoom != s.Room && s.PrevRoom is not null ? $", {s.PrevRoom}" : "") + ")";
                res.Add(new(id, e.Title, s.Begin, s.End, false, room, desc, e.MoodleUrl, "uspace", e.Title,
                    changed ? (s.Change == SessionChange.Moved ? "moved" : "changed") : "active", note));
            }
            if (showGhosts && changed && s.PrevBegin is { } pb && s.PrevEnd is { } pe && pb != s.Begin && pe >= from && pb < to)
                res.Add(new($"{id}:ghost", e.Title, pb, pe, false, s.PrevRoom, desc, e.MoodleUrl, "uspace", e.Title, "ghost",
                    s.Change == SessionChange.Moved ? $"Moved to {Fmt(s.Begin)}" : $"Now at {Fmt(s.Begin)}"));
        }
        return res;
    }

    // ---------- navigation

    void Today_Click(object s, RoutedEventArgs e) { _anchor = DateOnly.FromDateTime(DateTime.Today); Render(); }
    void Prev_Click(object s, RoutedEventArgs e) => Shift(-1);
    void Next_Click(object s, RoutedEventArgs e) => Shift(1);

    void Shift(int dir)
    {
        _anchor = _view switch
        {
            View.Day => _anchor.AddDays(dir),
            View.Week => _anchor.AddDays(7 * dir),
            _ => _anchor.AddMonths(dir),
        };
        Render();
    }

    void ViewBox_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _view = (View)Math.Max(0, ViewBox.SelectedIndex);
        if (_ready) Render();
    }

    void GoToDay(DateOnly d, View v)
    {
        _anchor = d;
        _view = v;
        ViewBox.SelectedIndex = (int)v;   // triggers Render via SelectionChanged
        if (ViewBox.SelectedIndex == (int)v && _ready) Render();
    }

    // ---------- render

    void Render()
    {
        if (!_ready) return;
        if (_scroller is not null && _scrollApplied) _scroll = _scroller.VerticalOffset;
        _scrollApplied = false;
        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        _scroller = null;

        if (_view == View.Month) RenderMonth(); else RenderTimeGrid();
    }

    // ----- week / day

    void RenderTimeGrid()
    {
        var n = _view == View.Week ? 7 : 1;
        var first = _view == View.Week ? CalendarModel.StartOfWeek(_anchor) : _anchor;
        var last = first.AddDays(n - 1);
        TitleText.Text = _view == View.Day ? DayLong(first)
            : first.Month == last.Month ? $"{first.Day}. – {last.ToString("d. MMMM yyyy", Culture)}"
            : $"{first.ToString("d. MMM", Culture)} – {last.ToString("d. MMM yyyy", Culture)}";

        var now = DateTimeOffset.Now;
        var events = LoadEvents(first, n);
        var meta = CalendarModel.BuildMetaRow(first, n, A.Db.GetAssignments(), events, now);

        Body.RowDefinitions.Add(new() { Height = GridLength.Auto });   // day header (sticky)
        Body.RowDefinitions.Add(new() { Height = GridLength.Auto });   // meta row (sticky)
        Body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        var header = DayColumns(n);
        for (var i = 0; i < n; i++) header.Children.Add(Place(DayHeader(first.AddDays(i)), 1 + i));
        Grid.SetRow(header, 0);
        Body.Children.Add(header);

        var metaGrid = BuildMetaGrid(first, n, meta);
        Grid.SetRow(metaGrid, 1);
        Body.Children.Add(metaGrid);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildTimeGrid(first, n, events, now) };
        Grid.SetRow(scroller, 2);
        Body.Children.Add(scroller);
        _scroller = scroller;
        // The extent is only known after the first layout pass.
        var applied = false;
        void Apply(object? s, object e)
        {
            if (applied || scroller.ScrollableHeight <= 0) return;
            applied = true;
            _scrollApplied = true;
            scroller.LayoutUpdated -= Apply;
            scroller.ChangeView(null, _scroll, null, true);
        }
        scroller.LayoutUpdated += Apply;
    }

    static Grid DayColumns(int n)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new() { Width = new GridLength(GutterW) });
        for (var i = 0; i < n; i++) g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    static FrameworkElement Place(FrameworkElement e, int col, int span = 1, int row = 0)
    {
        Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span); Grid.SetRow(e, row);
        return e;
    }

    static Border ColumnLine(int col, int rowSpan)
    {
        var b = new Border { BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
        Grid.SetColumn(b, col); Grid.SetRowSpan(b, rowSpan);
        return b;
    }

    FrameworkElement DayHeader(DateOnly d)
    {
        var today = d == DateOnly.FromDateTime(DateTime.Today);
        var num = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Center,
            Background = today ? Res("AccentFillColorDefaultBrush") : null,
            Child = new TextBlock
            {
                Text = d.Day.ToString(), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = today ? Res("TextOnAccentFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush"),
            },
        };
        var sp = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Spacing = 2, Background = null };
        sp.Children.Add(new TextBlock
        {
            Text = d.ToString("ddd", Culture).TrimEnd('.').ToUpperInvariant(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = today ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorSecondaryBrush"),
        });
        sp.Children.Add(num);
        sp.Tapped += (_, _) => GoToDay(d, View.Day);
        return sp;
    }

    // ----- sticky meta row

    Grid BuildMetaGrid(DateOnly first, int n, List<MetaPlacement> meta)
    {
        var g = DayColumns(n);
        var lanes = Math.Max(1, Math.Min(MaxLanes, meta.Count == 0 ? 1 : meta.Max(m => m.Lane) + 1));
        var overflowPerDay = new int[n];
        foreach (var m in meta.Where(m => m.Lane >= MaxLanes))
            for (var c = m.Col; c < m.Col + m.Span; c++) overflowPerDay[c]++;
        var rows = lanes + (overflowPerDay.Any(x => x > 0) ? 1 : 0);
        for (var r = 0; r < rows; r++) g.RowDefinitions.Add(new() { MinHeight = 26 });

        g.BorderBrush = Res("DividerStrokeColorDefaultBrush");
        g.BorderThickness = new Thickness(0, 1, 0, 1);
        g.Padding = new Thickness(0, 2, 0, 2);
        g.Background = Res("LayerFillColorDefaultBrush");

        for (var i = 0; i < n; i++) g.Children.Add(ColumnLine(1 + i, rows));
        var label = new TextBlock { Text = "due / all-day", FontSize = 10, Margin = new Thickness(4, 4, 4, 0), TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") };
        Grid.SetRowSpan(label, rows);
        g.Children.Add(label);

        foreach (var m in meta.Where(m => m.Lane < MaxLanes))
            g.Children.Add(Place(Chip(m.Item), 1 + m.Col, m.Span, m.Lane));

        for (var c = 0; c < n; c++)
        {
            if (overflowPerDay[c] == 0) continue;
            var day = first.AddDays(c);
            var btn = SmallButton($"+{overflowPerDay[c]} more");
            var dayItems = meta.Where(m => m.Item.StartDay <= day && m.Item.EndDay >= day).Select(m => m.Item).ToList();
            btn.Click += (_, _) => ShowListFlyout(btn, DayLong(day), dayItems);
            g.Children.Add(Place(btn, 1 + c, 1, lanes));
        }
        return g;
    }

    static Button SmallButton(string text) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
        Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left, Background = null, BorderThickness = new Thickness(0),
    };

    /// <summary>Minimal chip: title only (assignments prefixed by their due time). Click → details flyout.</summary>
    Button Chip(MetaItem item, bool compact = false)
    {
        string text;
        Brush bg, fg;
        if (item.Kind == MetaKind.Assignment)
        {
            var due = item.Assignment!.DueAt!.Value;
            text = (item.Submitted ? "✅ " : "") + (compact ? "" : Time(due) + "  ") + item.Title;
            (bg, fg) = item.Submitted ? (Res("SubtleFillColorSecondaryBrush"), Res("TextFillColorSecondaryBrush"))
                : item.Overdue ? (Res("SystemFillColorCriticalBackgroundBrush"), Res("TextFillColorPrimaryBrush"))
                : (Res("SystemFillColorCautionBackgroundBrush"), Res("TextFillColorPrimaryBrush"));
        }
        else
        {
            text = item.Title;
            (bg, fg) = (Res("AccentFillColorDefaultBrush"), Res("TextOnAccentFillColorPrimaryBrush"));
        }
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = fg },
            Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2, 1, 2, 1), MinHeight = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = bg, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
        };
        b.Click += (_, _) => ShowItemFlyout(b, item);
        return b;
    }

    // ----- time grid

    FrameworkElement BuildTimeGrid(DateOnly first, int n, List<CalendarEvent> events, DateTimeOffset now)
    {
        var g = DayColumns(n);
        g.Height = 24 * HourH;

        var gutter = new Canvas();
        for (var h = 1; h < 24; h++)
        {
            var t = new TextBlock { Text = $"{h:00}:00", FontSize = 10, Foreground = Res("TextFillColorSecondaryBrush") };
            Canvas.SetLeft(t, 8); Canvas.SetTop(t, h * HourH - 8);
            gutter.Children.Add(t);
        }
        Grid.SetColumn(gutter, 0);
        g.Children.Add(gutter);

        for (var i = 0; i < n; i++)
        {
            var day = first.AddDays(i);
            var canvas = new Canvas { Background = Res("LayerFillColorDefaultBrush") };
            canvas.SizeChanged += (_, e) => DrawDay(canvas, day, events, now, e.NewSize.Width);
            Grid.SetColumn(canvas, 1 + i);
            g.Children.Add(canvas);
            g.Children.Add(ColumnLine(1 + i, 1));
        }
        return g;
    }

    void DrawDay(Canvas canvas, DateOnly day, List<CalendarEvent> events, DateTimeOffset now, double width)
    {
        canvas.Children.Clear();
        if (width <= 0) return;
        for (var h = 0; h < 24; h++)
        {
            var line = new Rectangle { Width = width, Height = 1, Fill = Res("DividerStrokeColorDefaultBrush"), IsHitTestVisible = false };
            Canvas.SetTop(line, h * HourH);
            canvas.Children.Add(line);
        }

        foreach (var b in CalendarModel.LayoutTimed(day, events))
        {
            var w = (width - 6) / b.Columns;
            var top = b.StartMin / 60 * HourH;
            var h = Math.Max(18, (b.EndMin - b.StartMin) / 60 * HourH - 2);
            var st = b.Event.Status;
            var faded = st is "removed" or "ghost";
            var fg = faded ? Res("TextFillColorPrimaryBrush") : Res("TextOnAccentFillColorPrimaryBrush");
            var sp = new StackPanel { Spacing = 0 };
            sp.Children.Add(new TextBlock
            {
                Text = (st == "moved" ? "↪ " : st == "changed" ? "✎ " : "") + b.Event.Title, FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = fg,
                TextDecorations = st == "removed" ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
            });
            if (h > 34 && b.Event.Note is { Length: > 0 } nt && st is not "active")
                sp.Children.Add(new TextBlock { Text = nt, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = fg });
            if (h > 34)
                sp.Children.Add(new TextBlock { Text = $"{Time(b.Event.Start)} – {Time(b.Event.End)}", FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextOnAccentFillColorPrimaryBrush") });
            if (h > 50 && !string.IsNullOrEmpty(b.Event.Location))
                sp.Children.Add(new TextBlock { Text = b.Event.Location, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextOnAccentFillColorPrimaryBrush") });
            var btn = new Button
            {
                Content = sp, Width = Math.Max(10, w - 2), Height = h, Padding = new Thickness(5, 2, 5, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                Background = faded ? Res("SubtleFillColorSecondaryBrush") : Res("AccentFillColorDefaultBrush"),
                BorderBrush = st is "moved" or "changed" ? Res("SystemFillColorCautionBrush") : faded ? Res("TextFillColorSecondaryBrush") : null,
                BorderThickness = st is "moved" or "changed" or "removed" or "ghost" ? new Thickness(2) : new Thickness(0),
                CornerRadius = new CornerRadius(4), Opacity = st == "ghost" ? 0.55 : st == "removed" ? 0.75 : 1,
            };
            var ev = b.Event;
            btn.Click += (_, _) => ShowItemFlyout(btn, new MetaItem(MetaKind.Event, ev.Title, day, day, 0, null, ev));
            Canvas.SetLeft(btn, 2 + b.Column * w);
            Canvas.SetTop(btn, top + 1);
            canvas.Children.Add(btn);
        }

        if (day == DateOnly.FromDateTime(now.LocalDateTime))
        {
            var y = now.LocalDateTime.TimeOfDay.TotalMinutes / 60 * HourH;
            var red = Res("SystemFillColorCriticalBrush");
            var nowLine = new Rectangle { Width = width, Height = 2, Fill = red, IsHitTestVisible = false };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = red, IsHitTestVisible = false };
            Canvas.SetTop(nowLine, y - 1);
            Canvas.SetLeft(dot, -5); Canvas.SetTop(dot, y - 5);
            canvas.Children.Add(nowLine);
            canvas.Children.Add(dot);
        }
    }

    // ----- month

    void RenderMonth()
    {
        var firstOfMonth = new DateOnly(_anchor.Year, _anchor.Month, 1);
        var gridStart = CalendarModel.StartOfWeek(firstOfMonth);
        TitleText.Text = firstOfMonth.ToString("MMMM yyyy", Culture);

        var now = DateTimeOffset.Now;
        var events = LoadEvents(gridStart, 42);
        var assignments = A.Db.GetAssignments();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var g = new Grid();
        for (var c = 0; c < 7; c++) g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var r = 0; r < 6; r++) g.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        for (var c = 0; c < 7; c++)
        {
            var t = new TextBlock
            {
                Text = gridStart.AddDays(c).ToString("ddd", Culture).TrimEnd('.').ToUpperInvariant(), FontSize = 11, Margin = new Thickness(6, 2, 0, 4),
                Foreground = Res("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(t, c);
            g.Children.Add(t);
        }

        for (var i = 0; i < 42; i++)
        {
            var day = gridStart.AddDays(i);
            var items = new List<MetaItem>();
            items.AddRange(CalendarModel.BuildMetaRow(day, 1, assignments, events, now).Select(p => p.Item));
            var timed = events.Where(e => !CalendarModel.IsMeta(e) && CalendarModel.DayOf(e.Start) == day).OrderBy(e => e.Start)
                .Select(e => new MetaItem(MetaKind.Event, $"{Time(e.Start)} {e.Title}", day, day, 0, null, e)).ToList();
            items.AddRange(timed);

            var stack = new StackPanel { Spacing = 0 };
            var numBtn = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 2, 0, 2),
                Background = day == today ? Res("AccentFillColorDefaultBrush") : null,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = day == today ? Res("TextOnAccentFillColorPrimaryBrush")
                        : day.Month == firstOfMonth.Month ? Res("TextFillColorPrimaryBrush") : Res("TextFillColorDisabledBrush"),
                },
            };
            numBtn.Tapped += (_, _) => GoToDay(day, View.Day);
            stack.Children.Add(numBtn);

            foreach (var it in items.Take(3)) stack.Children.Add(Chip(it, compact: true));
            if (items.Count > 3)
            {
                var more = SmallButton($"+{items.Count - 3} more");
                var all = items;
                more.Click += (_, _) => ShowListFlyout(more, DayLong(day), all);
                stack.Children.Add(more);
            }

            var cell = new Border
            {
                BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1, 1, 0, 0),
                Background = day.Month == firstOfMonth.Month ? Res("LayerFillColorDefaultBrush") : null, Child = stack,
            };
            Grid.SetColumn(cell, i % 7); Grid.SetRow(cell, 1 + i / 7);
            g.Children.Add(cell);
        }
        Body.Children.Add(g);
    }

    // ---------- flyouts

    void ShowListFlyout(FrameworkElement anchor, string title, List<MetaItem> items)
    {
        var fly = new Flyout();
        var sp = new StackPanel { Spacing = 4, MinWidth = 240, MaxWidth = 360 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var it in items)
        {
            var chip = Chip(it);
            chip.Click += (_, _) => fly.Hide();   // Chip already opens its own details flyout
            sp.Children.Add(chip);
        }
        fly.Content = sp;
        fly.ShowAt(anchor);
    }

    void ShowItemFlyout(FrameworkElement anchor, MetaItem item)
    {
        var fly = new Flyout();
        var sp = new StackPanel { Spacing = 6, MinWidth = 260, MaxWidth = 380 };
        TextBlock T(string text, bool bold = false, bool secondary = false) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = secondary ? Res("TextFillColorSecondaryBrush") : Res("TextFillColorPrimaryBrush"),
        };

        if (item.Assignment is { } a)
        {
            sp.Children.Add(T(a.Name, bold: true));
            sp.Children.Add(T(a.CourseName, secondary: true));
            sp.Children.Add(T($"Due {a.DueAt!.Value.ToLocalTime().ToString("dddd, d. MMMM yyyy, HH:mm", Culture)}"));
            sp.Children.Add(T(a.Submitted ? "✅ Submitted" : item.Overdue ? "Overdue — not submitted" : "Not submitted"));
            if (a.Grade is not null) sp.Children.Add(T("Grade: " + a.Grade));
            var open = new Button { Content = "Open assignment", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            open.Click += (_, _) => { fly.Hide(); A.Window?.HandleCommandLine("--assignment=" + a.Id); };
            sp.Children.Add(open);
        }
        else if (item.Event is { } e)
        {
            sp.Children.Add(T(e.Title, bold: true));
            sp.Children.Add(T(EventWhen(e)));
            if (e.Note is { Length: > 0 } note) sp.Children.Add(T("⚠ " + note, bold: true));
            if (!string.IsNullOrWhiteSpace(e.CourseName)) sp.Children.Add(T(e.CourseName!, secondary: true));
            if (!string.IsNullOrWhiteSpace(e.Location)) sp.Children.Add(T("📍 " + e.Location));
            if (!string.IsNullOrWhiteSpace(e.Description)) sp.Children.Add(T(e.Description!, secondary: true));
            if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var link = new HyperlinkButton { Content = "Open link", Padding = new Thickness(0) };
                link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(uri);
                sp.Children.Add(link);
            }
            if (e.Source == "demo") sp.Children.Add(T("Demo event", secondary: true));
            if (e.Source == "uspace") sp.Children.Add(T("Source: u:space", secondary: true));
        }
        fly.Content = sp;
        fly.ShowAt(anchor);
    }
}
