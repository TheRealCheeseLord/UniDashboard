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
using MoodleDashboard.Core;
using MoodleDashboard.Core.Docx;
using MoodleDashboard.Core.Ufind;
using MoodleDashboard.Core.Univie;
using MoodleDashboard.Core.Uspace;

namespace MoodleDashboard.App;

public class CourseRow(EnrolmentRecord e)
{
    public EnrolmentRecord E { get; } = e;
    public string Title => E.Title;
    public string TypeText => string.IsNullOrWhiteSpace(E.Type) ? "—" : E.Type;
    public string Meta => $"{E.LvNr} · {E.Semester}";
    public Visibility UnseenVisibility => E.UnseenChange ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => E.Status == "ANGEMELDET" && E.Active ? Visibility.Collapsed : Visibility.Visible;
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
    EnrolmentRecord? _current;
    string? _showOnLoad;
    bool _selecting;

    public CoursesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Reload(); if (_showOnLoad is { } k) ShowCourse(k); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { if (e.Parameter is string key) _showOnLoad = key; }


    void Reload()
    {
        var list = A.Db.GetEnrolments();
        _selecting = true;
        CourseList.ItemsSource = list.Select(e => new CourseRow(e)).ToList();
        if (_current is { } c) CourseList.SelectedItem = ((List<CourseRow>)CourseList.ItemsSource).FirstOrDefault(r => r.E.Key == c.Key);
        _selecting = false;
        CountText.Text = list.Count == 0 ? "" : $"{list.Count} courses";
        EmptyHint.Visibility = list.Count == 0 && _current is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowCourse(string key)
    {
        if (!IsLoaded) { _showOnLoad = key; return; }
        Reload();
        Show(key);
    }

    void CourseList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_selecting && CourseList.SelectedItem is CourseRow r) Show(r.E.Key);
    }

    static string Best(Dictionary<string, string> d) =>
        d.TryGetValue("de", out var de) ? de : d.Values.FirstOrDefault() ?? "";

    void Show(string key)
    {
        var e = A.Db.GetEnrolment(key);
        if (e is null) return;
        _current = e;
        if (CourseList.ItemsSource is List<CourseRow> rows && (CourseList.SelectedItem as CourseRow)?.E.Key != key)
        {
            _selecting = true;
            CourseList.SelectedItem = rows.FirstOrDefault(r => r.E.Key == key);
            _selecting = false;
        }
        EmptyHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        TitleText.Text = e.Title;
        UfindCourse? uf = string.IsNullOrEmpty(e.UfindJson) ? null : JsonSerializer.Deserialize<UfindCourse>(e.UfindJson);
        MetaText.Text = $"{e.LvNr} · {uf?.TypeDesc ?? e.Type} · {e.Semester}" + (uf is null ? "" : $" · {uf.Ects:0.#} ECTS · {uf.Sws:0.#} SWS");
        StatusText.Text = "Status: " + (e.Active ? e.Status : "no longer enrolled") + (e.WaitlistPosition is { } w ? $" (waitlist #{w})" : "") +
            (e.StudyName is { Length: > 0 } sn ? $"\nStudy: {sn}" : "") + (e.Remark is { Length: > 0 } rm ? $"\nRemark: {rm}" : "");

        var lecturers = JsonSerializer.Deserialize<List<LecturerDto>>(e.LecturersJson) ?? [];
        LecturersText.Text = lecturers.Count == 0 ? "" : "Lecturers: " + string.Join(", ", lecturers.Select(l => $"{l.FirstName} {l.LastName}".Trim()));
        LecturersText.Visibility = LecturersText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var g = uf?.Groups.FirstOrDefault(x => x.GroupId == $"{e.LvNr}-{e.GroupNr}") ?? uf?.Groups.FirstOrDefault();
        RegText.Text = g is null ? "" :
            (g.EnrollFrom is { } f && g.EnrollTo is { } t ? $"Registration: {f.ToLocalTime():d.M.yyyy HH:mm} – {t.ToLocalTime():d.M.yyyy HH:mm}" : "") +
            (g.DisenrollUntil is { } du ? $"\nDeregistration until {du.ToLocalTime():d.M.yyyy HH:mm}" : "");
        RegText.Visibility = RegText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        MoodleButton.Visibility = e.MoodleUrl is null ? Visibility.Collapsed : Visibility.Visible;
        UfindButton.Visibility = e.UfindLink is null ? Visibility.Collapsed : Visibility.Visible;

        BuildInfo(g);
        BuildSessions();
        HistoryExpander.IsExpanded = false;
        HistoryList.ItemsSource = null;
        DiffPanel.Children.Clear();

        if (e.UnseenChange) { A.Db.ClearEnrolmentUnseen(e.Key); Reload(); }
    }

    void BuildInfo(UfindGroup? g)
    {
        InfoPanel.Children.Clear();
        if (g is null) return;
        void Section(string title, Dictionary<string, string> d)
        {
            var text = DocxGenerator.HtmlToPlainText(Best(d));
            if (text.Length == 0) return;
            InfoPanel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            InfoPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        Section("Description", g.Comment);
        Section("Assessment", g.Performance);
        Section("Examination", g.Examination);
        Section("Preconditions", g.Preconditions);
        Section("Literature", g.Literature);
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
            var text = $"{s.Begin.ToLocalTime().ToString("ddd d. MMM yyyy", De)}  {s.Begin.ToLocalTime():HH:mm}–{s.End.ToLocalTime():HH:mm}" +
                       (string.IsNullOrWhiteSpace(s.Room) ? "" : "  ·  " + s.Room);
            var label = s.Status == SessionStatus.Removed ? "  — cancelled"
                : s.Change == SessionChange.Moved && s.PrevBegin is { } pb ? $"  — moved from {pb.ToLocalTime().ToString("ddd d.M. HH:mm", De)}"
                : s.Change == SessionChange.Changed && s.PrevBegin is { } cb ? $"  — changed (was {cb.ToLocalTime().ToString("ddd d.M. HH:mm", De)}{(s.PrevRoom != s.Room && s.PrevRoom is not null ? ", " + s.PrevRoom : "")})"
                : s.Change == SessionChange.Added ? "  — new" : "";
            var tb = new TextBlock
            {
                Text = text + label, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                TextDecorations = s.Status == SessionStatus.Removed ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                Opacity = s.End < now ? 0.55 : s.Status == SessionStatus.Removed ? 0.7 : 1,
                Foreground = label.Length > 0 && s.Status != SessionStatus.Removed ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            SessionsPanel.Children.Add(tb);
        }
        if (SessionsPanel.Children.Count == 0)
            SessionsPanel.Children.Add(new TextBlock { Text = "No upcoming sessions.", Opacity = 0.7 });
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
