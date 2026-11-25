using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using UniDashboard.Core.Ufind;
using UniDashboard.Core.Univie;
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace UniDashboard.App;

/// <summary>The curriculum of the student's own study as a tree (section, module, course) with section exams and enrolled courses highlighted.</summary>
public sealed partial class StudyPlanPage : Page
{
    static App A => App.Current;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    sealed record PlanItem(string PathId, string Semester, string StudyName, CurriculumNode Root)
    {
        public override string ToString() => $"{(Root.Title.Length > 0 ? Root.Title : StudyName)} â€” {Semester}";
    }

    bool _loading = true;   // handlers fire while the XAML loads, before the controls exist

    public StudyPlanPage()
    {
        InitializeComponent();
        OnlyMine.IsOn = true;   // set here, not in XAML: Toggled would fire while the page is still being parsed
        Loaded += (_, _) => Reload();
    }

    void Reload()
    {
        _loading = true;
        var keep = (PlanBox.SelectedItem as PlanItem)?.PathId;
        var items = new List<PlanItem>();
        foreach (var (path, sem, study, json) in A.Db.GetCurriculumTrees())
        {
            try { if (JsonSerializer.Deserialize<CurriculumNode>(json) is { } root) items.Add(new(path, sem, study, root)); }
            catch (JsonException) { }
        }
        PlanBox.ItemsSource = items;
        PlanBox.SelectedItem = items.FirstOrDefault(i => i.PathId == keep) ?? items.FirstOrDefault();
        _loading = false;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TreeScroll.Visibility = DetailPanel.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanBox.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BuildTree();
    }

    void PlanBox_SelectionChanged(object s, SelectionChangedEventArgs e) { if (!_loading) BuildTree(); }
    void OnlyMine_Toggled(object s, RoutedEventArgs e) { if (!_loading) BuildTree(); }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // Colour coding: sections amber, courses blue, exams coral, the curriculum itself in the accent colour.
    static Windows.UI.Color SectionColor => Palette.Section;
    static Windows.UI.Color CourseColor => Palette.Course;
    static Windows.UI.Color ExamColor => Palette.Exam;

    /// <summary>What the detail panel shows for a clicked row.</summary>
    sealed record Detail(string Kind, Windows.UI.Color Color, string Glyph, string Title, string? Subtitle, List<(string Label, string Value)> Fields, string Url, string UrlText, string? CourseKey);

    const string Ufind = "https://ufind.univie.ac.at/de/";
    static string SectionUrl(string sem, string path) => $"{Ufind}vvz_sub.html?semester={Uri.EscapeDataString(sem)}&path={Uri.EscapeDataString(path)}";
    static string CourseUrl(string sem, string lv) => $"{Ufind}course.html?lv={Uri.EscapeDataString(lv)}&semester={Uri.EscapeDataString(sem)}";
    static string ExamUrl(string sem, string mod, string eq) => $"{Ufind}exam.html?mod={Uri.EscapeDataString(mod)}&eq={Uri.EscapeDataString(eq)}&semester={Uri.EscapeDataString(sem)}";
    static string ExamDateUrl(string sem, string mod, string prueid) => $"{Ufind}exam.html?prueid={Uri.EscapeDataString(prueid)}&mod={Uri.EscapeDataString(mod)}&semester={Uri.EscapeDataString(sem)}";

    Border? _selected;

    /// <summary>One selectable row: chevron slot, coloured icon chip, title + detail line, optional badge; hover and selection highlight.</summary>
    Border Row(Detail d, string sub, string badge, bool bold, bool dim, Brush titleBrush)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(2, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var chip = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, d.Color.R, d.Color.G, d.Color.B)),
            Child = new FontIcon { Glyph = d.Glyph, FontSize = 16, Foreground = new SolidColorBrush(d.Color) },
        };
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = d.Title, FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = titleBrush,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        if (badge.Length > 0)
        {
            var b = new Border
            {
                Background = Res("AccentFillColorSecondaryBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, FontSize = 12 },
            };
            Grid.SetColumn(b, 3);
            grid.Children.Add(b);
        }
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Opacity = dim ? 0.65 : 1, Tag = d };
        border.PointerEntered += (_, _) => { if (border != _selected) border.Background = Res("SubtleFillColorSecondaryBrush"); };
        border.PointerExited += (_, _) => { if (border != _selected) border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
        border.Tapped += (_, _) => Select(border);
        return border;
    }

    void Select(Border row)
    {
        if (_selected is not null) _selected.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _selected = row;
        row.Background = Res("SubtleFillColorTertiaryBrush");
        ShowDetail((Detail)row.Tag);
    }

    void ClearDetail()
    {
        _selected = null;
        DetailHost.Children.Clear();
        DetailHost.Children.Add(new TextBlock
        {
            Text = "Select a section, exam or course to see its details here.", TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Foreground = Res("TextFillColorSecondaryBrush"),
        });
    }

    void ShowDetail(Detail d)
    {
        DetailHost.Children.Clear();
        var chip = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, d.Color.R, d.Color.G, d.Color.B)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 2, 10, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = d.Glyph, FontSize = 12, Foreground = new SolidColorBrush(d.Color) },
                    new TextBlock { Text = d.Kind, FontSize = 12, Foreground = new SolidColorBrush(d.Color), FontWeight = FontWeights.SemiBold },
                },
            },
        };
        DetailHost.Children.Add(chip);
        DetailHost.Children.Add(new TextBlock { Text = d.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (!string.IsNullOrEmpty(d.Subtitle)) DetailHost.Children.Add(new TextBlock { Text = d.Subtitle, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        if (Uri.TryCreate(d.Url, UriKind.Absolute, out var uri))
        {
            var open = new Button
            {
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = d.UrlText } } },
            };
            open.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(uri);
            buttons.Children.Add(open);
        }
        if (d.CourseKey is { } key)
        {
            var show = new Button { Content = "Show details" };
            ToolTipService.SetToolTip(show, "Open this course in the Courses page");
            show.Click += (_, _) => A.Window.HandleCommandLine("--course=" + key);
            buttons.Children.Add(show);
        }
        DetailHost.Children.Add(buttons);

        foreach (var (label, value) in d.Fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var f = new StackPanel { Spacing = 1 };
            f.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush") });
            f.Children.Add(new TextBlock { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            DetailHost.Children.Add(f);
        }
    }

    void BuildTree()
    {
        TreeHost.Children.Clear();
        ClearDetail();
        if (PlanBox.SelectedItem is not PlanItem item) { SummaryText.Text = ""; return; }

        var enrolments = A.Db.GetEnrolments().Where(e => e.Semester == item.Semester).GroupBy(e => e.LvNr).ToDictionary(g => g.Key, g => g.First());
        var onlyMine = OnlyMine.IsOn;
        var now = DateTimeOffset.Now;

        int MyCourses(CurriculumNode n) => n.Flatten().SelectMany(x => x.Courses).Count(c => enrolments.ContainsKey(c.LvNr));
        string D(ExamRecord x) => x.TimeKnown
            ? x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy, HH:mm", De) + "–" + x.End.ToLocalTime().ToString("HH:mm", De)
            : x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De) + " (time not published yet)";

        var accent = Res("AccentTextFillColorPrimaryBrush");
        var primary = Res("TextFillColorPrimaryBrush");
        var secondary = Res("TextFillColorSecondaryBrush");
        var guideStroke = Res("CardStrokeColorDefaultBrush");
        var accentColor = Palette.Curriculum;

        var sections = 0; var upcoming = new List<ExamRecord>();

        UIElement Build(CurriculumNode n, bool isRoot, string parentTitle)
        {
            var mine = MyCourses(n);
            var title = n.Title.Length > 0 ? n.Title : item.StudyName;
            var exams = n.Exams.Count > 0
                ? A.Db.GetExams(EnrolmentRecord.SectionKey(item.Semester, n.PathId)).Where(x => x.Status == ExamStatus.Active).ToList() : [];
            var next = exams.Where(x => x.End >= now).OrderBy(x => x.Begin).FirstOrDefault();
            var sub = new List<string>();
            if (mine > 0 && !isRoot) sub.Add(mine == 1 ? "1 of your courses" : $"{mine} of your courses");
            if (next is not null) sub.Add("next exam " + next.Begin.ToLocalTime().ToString("ddd d. MMM", De));
            if (mine > 0 && !isRoot && n.Exams.Count > 0) sections++;

            var fields = new List<(string, string)>();
            if (parentTitle.Length > 0) fields.Add(("Part of", parentTitle));
            fields.Add(("Contains", $"{n.Courses.Count} course(s), {n.Children.Count} subsection(s)"));
            var myNames = n.Courses.Where(c => enrolments.ContainsKey(c.LvNr)).Select(c => "• " + c.Title).ToList();
            if (myNames.Count > 0) fields.Add(("Your courses here", string.Join("\n", myNames)));
            if (n.Exams.Count > 0)
                fields.Add(("Exam", exams.Count == 0 ? n.Exams[0].Title + " — no dates fetched yet" :
                    string.Join("\n", exams.Select(x => "• " + D(x) + (string.IsNullOrWhiteSpace(x.Room) ? "" : " · " + x.Room) + (x.Registered ? " ✔ registered" : "")))));
            var detail = new Detail(isRoot ? "Curriculum" : "Section", isRoot ? accentColor : SectionColor, isRoot ? "" : "", title,
                isRoot ? item.StudyName + " · " + item.Semester : null, fields, SectionUrl(item.Semester, n.PathId), "Open in u:find", null);
            var header = Row(detail, string.Join(" · ", sub), exams.Any(x => x.Registered && x.End >= now) ? "registered" : "", mine > 0 || isRoot, false, mine > 0 || isRoot ? primary : secondary);

            var body = new StackPanel { Spacing = 0 };
            foreach (var x in exams)
            {
                var past = x.End < now;
                if (!past) upcoming.Add(x);
                var xd = new Detail("Exam", ExamColor, x.Registered ? "" : "", x.Title, title,
                    [("Date", D(x)), ("Room", x.Room ?? ""), ("Registration", x.Registered ? "You are registered" : "Not registered (register in u:space)"), ("Section", title)],
                    x.ExtId.StartsWith("sect:") ? ExamDateUrl(item.Semester, n.PathId, x.ExtId["sect:".Length..]) : ExamUrl(item.Semester, n.Exams[0].ModPath, n.Exams[0].Eq),
                    "Open exam in u:find", null);
                body.Children.Add(Row(xd, D(x) + (string.IsNullOrWhiteSpace(x.Room) ? "" : " · " + x.Room), x.Registered ? "registered" : "", true, past, primary));
            }
            if (n.Exams.Count > 0 && exams.Count == 0)
            {
                var xd = new Detail("Exam", ExamColor, "", n.Exams[0].Title, title, [("Dates", mine > 0 ? "No exam dates published yet" : "Dates are fetched once you are enrolled in a course of this section"), ("Section", title)],
                    ExamUrl(item.Semester, n.Exams[0].ModPath, n.Exams[0].Eq), "Open exam in u:find", null);
                body.Children.Add(Row(xd, xd.Fields[0].Value, "", false, mine == 0, secondary));
            }
            foreach (var c in n.Courses)
            {
                var isMine = enrolments.TryGetValue(c.LvNr, out var enr);
                if (onlyMine && !isMine) continue;
                var cd = new Detail("Course", CourseColor, "", c.Title, $"{c.LvNr} · {c.Type}",
                    [("Status", isMine ? "You are enrolled (" + enr!.Status.ToLowerInvariant() + ")" : "Not enrolled"), ("Section", title)],
                    CourseUrl(item.Semester, c.LvNr), "Open in u:find", isMine ? enr!.Key : null);
                body.Children.Add(Row(cd, $"{c.LvNr} · {c.Type}", "", isMine, !isMine, isMine ? primary : secondary));
            }
            foreach (var ch in n.Children)
            {
                if (onlyMine && MyCourses(ch) == 0) continue;
                body.Children.Add(Build(ch, false, title));
            }

            var container = new StackPanel();
            container.Children.Add(header);
            if (body.Children.Count == 0) return container;

            // Indented body with a thin guide line; the chevron (not the row) expands/collapses it.
            var guide = new Border
            {
                Child = body, Margin = new Thickness(13, 0, 0, 0), Padding = new Thickness(11, 0, 0, 0),
                BorderBrush = guideStroke, BorderThickness = new Thickness(1, 0, 0, 0),
                Visibility = isRoot || mine > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            container.Children.Add(guide);
            var chev = new FontIcon { FontSize = 11, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            void SyncChev() => chev.Glyph = guide.Visibility == Visibility.Visible ? "" : "";
            SyncChev();
            var hit = new Border { Child = chev, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Width = 24 };
            hit.Tapped += (_, e) =>
            {
                e.Handled = true;   // do not also select the row
                guide.Visibility = guide.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                SyncChev();
            };
            ToolTipService.SetToolTip(hit, "Expand / collapse");
            if (header.Child is Grid hg) { hg.Children.Add(hit); }
            return container;
        }

        TreeHost.Children.Add(Build(item.Root, true, ""));

        var total = MyCourses(item.Root);
        var nextExam = upcoming.OrderBy(x => x.Begin).FirstOrDefault();
        SummaryText.Text = $"{item.StudyName} · {item.Semester} — {total} of your courses in this plan, {sections} section(s) with an exam" +
                           (nextExam is not null ? $", next: {nextExam.Title} on {nextExam.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}" : "") + ".";
    }

    async void SyncButton_Click(object s, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false; Busy.IsActive = true; Info.IsOpen = false;
        try
        {
            var r = await Task.Run(() => A.Sync.Univie!.RunAsync());
            Info.Severity = r.Error is null ? InfoBarSeverity.Success : r.LoginRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            Info.Message = r.Error is null ? "Synced courses, study plan and section exams." : r.Error;
            Info.IsOpen = true;
        }
        catch (Exception ex) { Info.Severity = InfoBarSeverity.Error; Info.Message = ex.Message; Info.IsOpen = true; }
        finally { Busy.IsActive = false; SyncButton.IsEnabled = true; Reload(); }
    }
}
