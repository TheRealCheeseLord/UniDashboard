using System.Globalization;
using System.Text.Json;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using UniDashboard.Core;
using UniDashboard.Core.Docx;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Univie;
using UniDashboard.Core.Uspace;

namespace UniDashboard.App;

public class CourseRow(EnrolmentRecord e)
{
    public EnrolmentRecord E { get; } = e;
    public string Title => E.Title;
    public string TypeText => string.IsNullOrWhiteSpace(E.Type) ? "—" : E.Type;
    public bool ShowBadge => !(E.Status == "ANGEMELDET" && E.Active);
    public string BadgeText => !E.Active ? "not enrolled" : E.Status switch
    {
        "WARTELISTE" => E.WaitlistPosition is { } w ? $"waitlist #{w}" : "waitlist",
        var s => s.ToLowerInvariant(),
    };
}

public sealed partial class CoursesPage : Page
{
    static App A => App.Current;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");
    static Windows.UI.Color CourseColor => Palette.Course;
    static Windows.UI.Color ExamColor => Palette.Exam;
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static Brush Tint(Windows.UI.Color c, byte a = 0x30) => new SolidColorBrush(Windows.UI.Color.FromArgb(a, c.R, c.G, c.B));
    static Brush Clear => new SolidColorBrush(Colors.Transparent);

    EnrolmentRecord? _current;
    string? _showOnLoad;
    readonly Dictionary<string, Border> _rows = [];
    Border? _selected;

    public CoursesPage()
    {
        InitializeComponent();
        SessionsIcon.Foreground = new SolidColorBrush(CourseColor);
        Loaded += (_, _) => { Reload(); if (_showOnLoad is { } k) ShowCourse(k); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { if (e.Parameter is string key) _showOnLoad = key; }

    // "2026W" sorts after "2026S" of the same year; newer years first.
    static int SemOrder(string sem) => sem.Length >= 5 && int.TryParse(sem[..4], out var y) ? y * 2 + (sem[4] == 'W' ? 1 : 0) : 0;

    static string SemLabel(string sem) => sem.Length >= 5 ? (sem[4] == 'W' ? "Winter semester " : "Summer semester ") + sem[..4] : sem;

    void Reload()
    {
        var list = A.Db.GetEnrolments();
        CourseHost.Children.Clear(); _rows.Clear(); _selected = null;
        foreach (var grp in list.GroupBy(e => e.Semester).OrderByDescending(g => SemOrder(g.Key)))
        {
            CourseHost.Children.Add(new TextBlock
            {
                Text = $"{SemLabel(grp.Key)} · {grp.Count()}", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(6, CourseHost.Children.Count == 0 ? 0 : 14, 0, 4),
            });
            foreach (var e in grp) { var row = BuildRow(new CourseRow(e)); _rows[e.Key] = row; CourseHost.Children.Add(row); }
        }
        if (_current is { } c && _rows.TryGetValue(c.Key, out var cur)) { _selected = cur; cur.Background = Res("SubtleFillColorTertiaryBrush"); }
        CountText.Text = list.Count == 0 ? "" : $"{list.Count} courses";
        EmptyHint.Visibility = list.Count == 0 && _current is null ? Visibility.Visible : Visibility.Collapsed;
    }

    Border BuildRow(CourseRow r)
    {
        var dim = !r.E.Active;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (r.E.UnseenChange)
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Res("AccentFillColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = Tint(CourseColor),
            Child = new FontIcon { Glyph = "\uE8F1", FontSize = 18, Foreground = new SolidColorBrush(CourseColor) },
        };
        Grid.SetColumn(chip, 1); grid.Children.Add(chip);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(new Border { Background = Tint(CourseColor, 0x28), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Child = new TextBlock { Text = r.TypeText, FontSize = 12 } });
        meta.Children.Add(new TextBlock { Text = r.E.LvNr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") });
        text.Children.Add(meta);
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        if (r.ShowBadge)
        {
            var b = new Border
            {
                Background = Res("SystemFillColorCautionBackgroundBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = r.BadgeText, FontSize = 12 },
            };
            Grid.SetColumn(b, 3); grid.Children.Add(b);
        }
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(6), Background = Clear, Opacity = dim ? 0.6 : 1 };
        border.PointerEntered += (_, _) => { if (border != _selected) border.Background = Res("SubtleFillColorSecondaryBrush"); };
        border.PointerExited += (_, _) => { if (border != _selected) border.Background = Clear; };
        border.Tapped += (_, _) => Show(r.E.Key);
        return border;
    }

    public void ShowCourse(string key)
    {
        if (!IsLoaded) { _showOnLoad = key; return; }
        Reload();
        Show(key);
    }

    static string Best(Dictionary<string, string> d) =>
        d.TryGetValue("de", out var de) ? de : d.Values.FirstOrDefault() ?? "";

    void Show(string key)
    {
        var e = A.Db.GetEnrolment(key);
        if (e is null) return;
        _current = e;
        if (_selected is not null) _selected.Background = Clear;
        if (_rows.TryGetValue(key, out var row)) { _selected = row; row.Background = Res("SubtleFillColorTertiaryBrush"); }
        EmptyHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        UfindCourse? uf = string.IsNullOrEmpty(e.UfindJson) ? null : JsonSerializer.Deserialize<UfindCourse>(e.UfindJson);
        KindChip.Background = Tint(CourseColor); KindIcon.Foreground = new SolidColorBrush(CourseColor); KindText.Foreground = new SolidColorBrush(CourseColor);
        KindText.Text = "Course · " + (uf?.TypeDesc is { Length: > 0 } td ? td : e.Type);
        TitleText.Text = e.Title;
        MetaText.Text = $"{e.LvNr} · {e.Semester}" + (uf is null ? "" : $" · {uf.Ects:0.#} ECTS · {uf.Sws:0.#} SWS");

        FactsPanel.Children.Clear();
        var (sg, sc, st) = !e.Active ? ("\uE711", Palette.Muted, "No longer enrolled")
            : e.Status == "ANGEMELDET" ? ("\uE73E", Palette.Done, "Enrolled")
            : ("\uE916", Palette.Warning, e.Status == "WARTELISTE" ? "On the waitlist" + (e.WaitlistPosition is { } w ? $" (#{w})" : "") : e.Status.ToLowerInvariant());
        FactsPanel.Children.Add(IconLine(sg, sc, st + (e.Remark is { Length: > 0 } rm ? " · " + rm : "")));
        if (e.StudyName is { Length: > 0 } sn) FactsPanel.Children.Add(IconLine("\uE7BE", Palette.Curriculum, sn));
        var lecturers = JsonSerializer.Deserialize<List<LecturerDto>>(e.LecturersJson) ?? [];
        if (lecturers.Count > 0) FactsPanel.Children.Add(IconLine("\uE77B", CourseColor, string.Join(", ", lecturers.Select(l => $"{l.FirstName} {l.LastName}".Trim()))));

        var g = uf?.Groups.FirstOrDefault(x => x.GroupId == $"{e.LvNr}-{e.GroupNr}") ?? uf?.Groups.FirstOrDefault();
        var reg = g is null ? "" :
            (g.EnrollFrom is { } f && g.EnrollTo is { } t ? $"Registration {f.ToLocalTime():d.M.yyyy HH:mm} – {t.ToLocalTime():d.M.yyyy HH:mm}" : "") +
            (g.DisenrollUntil is { } du ? $"\nDeregistration until {du.ToLocalTime():d.M.yyyy HH:mm}" : "");
        if (reg.Length > 0) FactsPanel.Children.Add(IconLine("\uE8F2", Palette.Curriculum, reg.TrimStart('\n')));

        MoodleButton.Visibility = e.MoodleUrl is null ? Visibility.Collapsed : Visibility.Visible;
        UfindButton.Visibility = e.UfindLink is null ? Visibility.Collapsed : Visibility.Visible;

        BuildInfo(g);
        BuildSessions();
        BuildExams();
        HistoryExpander.IsExpanded = false;
        HistoryList.ItemsSource = null;
        DiffPanel.Children.Clear();

        if (e.UnseenChange) { A.Db.ClearEnrolmentUnseen(e.Key); Reload(); }
    }

    static FrameworkElement IconLine(string glyph, Windows.UI.Color color, string text, Brush? fg = null)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new() { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var tb = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (fg is not null) tb.Foreground = fg;
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        return g;
    }

    static FrameworkElement SectionHeader(string glyph, Windows.UI.Color color, string title) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 2),
        Children = { new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(color) }, new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] } },
    };

    /// <summary>A dated line (session or exam): coloured icon chip and text, same look as the Study plan rows.</summary>
    static FrameworkElement ItemRow(string glyph, Windows.UI.Color color, string title, string sub, double opacity, Brush fg, bool strike)
    {
        var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 5, 0, 5), Opacity = opacity };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top, Background = Tint(color), Child = new FontIcon { Glyph = glyph, FontSize = 15, Foreground = new SolidColorBrush(color) } });
        var st = new StackPanel { Spacing = 1 };
        var deco = strike ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
        st.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = fg, TextDecorations = deco });
        if (sub.Length > 0) st.Children.Add(new TextBlock { Text = sub, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Res("TextFillColorSecondaryBrush") });
        Grid.SetColumn(st, 1); g.Children.Add(st);
        return g;
    }

    void BuildInfo(UfindGroup? g)
    {
        InfoPanel.Children.Clear();
        if (g is null) return;
        void Section(string glyph, string title, Dictionary<string, string> d)
        {
            var text = DocxGenerator.HtmlToPlainText(Best(d));
            if (text.Length == 0) return;
            InfoPanel.Children.Add(SectionHeader(glyph, CourseColor, title));
            InfoPanel.Children.Add(new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        Section("\uE8A5", "Description", g.Comment);
        Section("\uE9D9", "Assessment", g.Performance);
        Section("\uE70B", "Examination", g.Examination);
        Section("\uE7BA", "Preconditions", g.Preconditions);
        Section("\uE82D", "Literature", g.Literature);
    }

    void BuildExams()
    {
        ExamsPanel.Children.Clear();
        if (_current is null) return;
        var now = DateTimeOffset.Now;
        var exams = A.Db.GetExams(_current.Key).Where(x => x.Status == ExamStatus.Active || A.Settings.ShowMovedRemoved && x.Begin > now.AddDays(-7)).ToList();
        if (exams.Count == 0) return;

        ExamsPanel.Children.Add(SectionHeader("\uE787", ExamColor, "Exams"));
        string Dt(DateTimeOffset d) => d.ToLocalTime().ToString("ddd d.M. HH:mm", De);
        foreach (var x in exams)
        {
            var when = x.TimeKnown ? x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy, HH:mm", De) + "–" + x.End.ToLocalTime().ToString("HH:mm", De)
                                   : x.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De) + " (time not published yet)";
            var reg = !x.HasWindow ? "registration window not published yet"
                : now < x.EnrollFrom ? $"registration opens {Dt(x.EnrollFrom!.Value)} – {Dt(x.EnrollTo!.Value)}"
                : now <= x.EnrollTo ? $"registration OPEN until {Dt(x.EnrollTo!.Value)}"
                : $"registration closed ({Dt(x.EnrollTo!.Value)})";
            var status = x.Status == ExamStatus.Removed ? " — removed"
                : x.Change == ExamChange.Changed ? " — changed" + (x.PrevBegin is { } pb && pb != x.Begin ? $" (was {Dt(pb)})" : "") : "";
            var sub = (x.Registered ? "✔ registered · " : "") + reg + (x.DisenrollUntil is { } du && x.HasWindow ? $" · deregister until {Dt(du)}" : "") +
                      (string.IsNullOrWhiteSpace(x.When) ? "" : "\n" + DocxGenerator.HtmlToPlainText(x.When!).Replace("\n", " "));
            ExamsPanel.Children.Add(ItemRow(x.Registered ? "\uE73E" : "\uE787", ExamColor, when + status, sub,
                x.End < now ? 0.55 : x.Status == ExamStatus.Removed ? 0.7 : 1,
                x.Registered ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush"), x.Status == ExamStatus.Removed));
        }
    }

    void PastSwitch_Toggled(object s, RoutedEventArgs e) { if (_current is not null) BuildSessions(); }

    void BuildSessions()
    {
        SessionsPanel.Children.Clear();
        if (_current is null) return;
        var now = DateTimeOffset.Now;
        var all = A.Db.GetSessions(_current.Key);
        foreach (var s in all.Where(s => PastSwitch.IsOn || s.End > now || s.Status == SessionStatus.Removed && s.Begin > now.AddDays(-7)))
        {
            if (s.Status == SessionStatus.Removed && !A.Settings.ShowMovedRemoved) continue;
            var title = $"{s.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  {s.Begin.ToLocalTime():HH:mm}–{s.End.ToLocalTime():HH:mm}";
            var label = s.Status == SessionStatus.Removed ? "cancelled"
                : s.Change == SessionChange.Moved && s.PrevBegin is { } pb ? $"moved from {pb.ToLocalTime().ToString("ddd d.M. HH:mm", De)}"
                : s.Change == SessionChange.Changed && s.PrevBegin is { } cb ? $"changed (was {cb.ToLocalTime().ToString("ddd d.M. HH:mm", De)}{(s.PrevRoom != s.Room && s.PrevRoom is not null ? ", " + s.PrevRoom : "")})"
                : s.Change == SessionChange.Added ? "new" : "";
            var sub = string.Join(" · ", new[] { string.IsNullOrWhiteSpace(s.Room) ? "" : s.Room, label }.Where(x => x.Length > 0));
            var changed = label.Length > 0 && s.Status != SessionStatus.Removed;
            SessionsPanel.Children.Add(ItemRow(s.Status == SessionStatus.Removed ? "\uE711" : changed ? "\uE81C" : "\uE823", CourseColor, title, sub,
                s.End < now ? 0.55 : s.Status == SessionStatus.Removed ? 0.7 : 1,
                changed ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush"), s.Status == SessionStatus.Removed));
        }
        if (SessionsPanel.Children.Count == 0)
            SessionsPanel.Children.Add(new TextBlock { Text = "No upcoming sessions.", FontSize = 14, Opacity = 0.7 });
    }

    async void MoodleButton_Click(object s, RoutedEventArgs e)
    { if (_current?.MoodleUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri)) await Windows.System.Launcher.LaunchUriAsync(uri); }

    async void UfindButton_Click(object s, RoutedEventArgs e)
    { if (_current?.UfindLink is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri)) await Windows.System.Launcher.LaunchUriAsync(uri); }

    // ---------- history

    record SnapView(Snapshot Snap, string Label);

    void HistoryExpander_Expanding(Expander s, ExpanderExpandingEventArgs a)
    {
        if (_current is null) return;
        var snaps = A.Db.GetEnrolmentSnapshots(_current.Key);
        HistoryList.ItemsSource = snaps.Select(x => new SnapView(x, $"{x.TakenAt.ToLocalTime():g} — {x.Label}")).ToList();
        HistoryList.DisplayMemberPath = nameof(SnapView.Label);
        if (snaps.Count > 0) HistoryList.SelectedIndex = 0;
    }

    void HistoryList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        DiffPanel.Children.Clear();
        if (HistoryList.SelectedItem is not SnapView sv || _current is null) return;
        var snaps = A.Db.GetEnrolmentSnapshots(_current.Key);
        var idx = snaps.FindIndex(x => x.Id == sv.Snap.Id);
        var older = idx + 1 < snaps.Count ? snaps[idx + 1] : null;
        var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(SnapshotText.Render(older?.Json), SnapshotText.Render(sv.Snap.Json), ignoreWhitespace: false);
        foreach (var line in diff.Lines)
        {
            if (line.Type == ChangeType.Imaginary) continue;
            var (prefix, bg) = line.Type switch
            {
                ChangeType.Inserted => ("+ ", Windows.UI.Color.FromArgb(0x40, 0x2E, 0xA0, 0x43)),
                ChangeType.Deleted => ("− ", Windows.UI.Color.FromArgb(0x40, 0xD1, 0x34, 0x38)),
                _ => ("  ", Colors.Transparent),
            };
            DiffPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(bg), Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock { Text = prefix + line.Text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 },
            });
        }
    }

    // ---------- sync

    async void SyncButton_Click(object s, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false; Busy.IsActive = true; Info.IsOpen = false;
        try
        {
            var r = await Task.Run(() => A.Sync.Univie!.RunAsync());
            Info.Severity = r.Error is null ? InfoBarSeverity.Success : r.LoginRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            Info.Message = r.Error is null ? $"Synced {r.Enrolments} courses, {r.Changed} changed." : r.Error;
            Info.IsOpen = true;
        }
        catch (Exception ex) { Info.Severity = InfoBarSeverity.Error; Info.Message = ex.Message; Info.IsOpen = true; }
        finally { Busy.IsActive = false; SyncButton.IsEnabled = true; Reload(); if (_current is { } c) Show(c.Key); }
    }
}
