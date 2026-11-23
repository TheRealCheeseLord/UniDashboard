using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Planning;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.App;

public sealed partial class PlannerPage : Page
{
    static App A => App.Current;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");
    bool _loading = true;

    // Enrolled courses that have exam dates (or a registered exam).
    List<PlanCourse> _courses = [];
    PlanResult? _result;

    public PlannerPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            foreach (RadioButton rb in StrategyButtons.Items)
                if ((string)rb.Tag == A.Settings.PlannerStrategy.ToString()) StrategyButtons.SelectedItem = rb;
            _loading = false;
            Load();
            Refresh();
        };
    }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    void Load()
    {
        var exams = A.Db.GetAllExams();
        _courses = exams.GroupBy(x => x.Enrolment.Key)
            .Select(g => new PlanCourse(g.Key, g.First().Enrolment.Title, g.Select(x => x.Exam).ToList()))
            .Where(c => c.Options.Any(o => o.Status == ExamStatus.Active))
            .OrderBy(c => c.Title).ToList();
    }

    PlanSettings CurrentSettings() => new()
    {
        Strategy = A.Settings.PlannerStrategy,
        Excluded = A.Settings.PlannerExcluded,
        Pins = A.Settings.PlannerPins,
    };

    static string Describe(ExamRecord x) => x.TimeKnown
        ? $"{x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  {x.Begin.ToLocalTime():HH:mm}–{x.End.ToLocalTime():HH:mm}"
        : $"{x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  (time not published yet)";

    void Strategy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StrategyButtons.SelectedItem is not RadioButton rb) return;
        A.Settings.PlannerStrategy = Enum.Parse<PlanStrategy>((string)rb.Tag);
        Refresh();
    }

    void ResetButton_Click(object s, RoutedEventArgs e) { A.Settings.PlannerPins = new(); Refresh(); }

    void CalendarButton_Click(object s, RoutedEventArgs e)
    {
        if (_result?.Timeline.FirstOrDefault()?.Chosen is { } first)
            A.Window?.HandleCommandLine($"--calendar=week@{first.Begin.ToLocalTime():yyyy-MM-dd}");
    }

    // ---------- plan

    void Refresh()
    {
        var now = DateTimeOffset.Now;
        var settings = CurrentSettings();
        var busy = A.Db.GetSessionsBetween(now.AddDays(-1), now.AddYears(2))
            .Where(x => x.Session.Status == SessionStatus.Active)
            .Select(x => new PlanBusy(x.Session.Begin, x.Session.End, x.Enrolment.Title)).ToList();

        _result = ExamPlanner.Plan(_courses, busy, settings, now);
        StrategyHint.Text = settings.Strategy switch
        {
            PlanStrategy.Earliest => "Earliest date of every course.",
            PlanStrategy.Latest => "Latest date of every course — the most time to study.",
            _ => "Maximises the days between your exams (smallest gap first).",
        };
        ResetButton.IsEnabled = settings.Pins.Count > 0;
        CalendarButton.IsEnabled = _result.Timeline.Count > 0;
        BuildSummary(_result);
        BuildCourses(_result, settings);
    }

    void BuildSummary(PlanResult r)
    {
        if (r.Timeline.Count == 0)
        {
            TimelineText.Text = _courses.Count == 0 ? "None of your courses has published exam dates yet." : "No course selected / no plannable exam.";
            StatsText.Text = "";
            return;
        }
        var parts = new List<string>();
        for (var i = 0; i < r.Timeline.Count; i++)
        {
            var e = r.Timeline[i];
            if (i > 0) parts.Add($"  ·· {r.Gaps[i - 1]} d ··  ");
            parts.Add($"{e.Course.Title.Split(':')[0]} {e.Chosen!.Begin.ToLocalTime():d.M.}");
        }
        TimelineText.Text = string.Concat(parts);
        var clashes = r.Entries.Count(e => e.Warnings.Any(w => w.StartsWith("⚠")));
        StatsText.Text = (r.Gaps.Count > 0 ? $"Smallest gap: {r.Gaps.Min()} days · " : "") +
                         (clashes == 0 ? "no clashes" : $"{clashes} course(s) with a clash") +
                         (r.Entries.Any(e => e.UnplannedReason is not null) ? $" · {r.Entries.Count(e => e.UnplannedReason is not null)} unplanned" : "");
    }

    void BuildCourses(PlanResult r, PlanSettings settings)
    {
        CoursePanel.Children.Clear();
        foreach (var course in _courses)
        {
            var entry = r.Entries.FirstOrDefault(e => e.Course.Key == course.Key);
            CoursePanel.Children.Add(Card(course, entry, settings));
        }
    }

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
            {
                var badges = new List<string>();
                if (entry.FixedReason == "registered") badges.Add("✅ registered");
                if (entry.FixedReason == "pinned") badges.Add("📌 pinned");
                stack.Children.Add(new TextBlock
                {
                    Text = "Recommended: " + Describe(c) + (badges.Count > 0 ? "   " + string.Join("  ", badges) : ""),
                    FontSize = 15, Margin = new Thickness(28, 0, 0, 0), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                });
                foreach (var w in entry.Warnings)
                    stack.Children.Add(Warning(w));
            }
            else
            {
                stack.Children.Add(new TextBlock { Text = "Not planned: " + entry.UnplannedReason, Margin = new Thickness(28, 0, 0, 0), Foreground = Res("SystemFillColorCautionBrush") });
            }

            if (entry.Options.Count > 0 && entry.FixedReason != "registered")
                stack.Children.Add(Alternatives(entry, settings));
        }
        else
            stack.Children.Add(new TextBlock { Text = "Excluded from the plan", Margin = new Thickness(28, 0, 0, 0), Opacity = 0.6 });

        return new Border
        {
            Child = stack, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8),
            Background = Res("LayerFillColorDefaultBrush"), BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            Opacity = included ? 1 : 0.75,
        };
    }

    TextBlock Warning(string w) => new()
    {
        Text = w, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
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
}
