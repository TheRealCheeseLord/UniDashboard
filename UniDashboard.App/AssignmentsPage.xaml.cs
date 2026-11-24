using System.Text.Json;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using UniDashboard.Core;
using UniDashboard.Core.Docx;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace UniDashboard.App;

public class AssignmentRow
{
    public Assignment Model { get; init; } = null!;
    public string Title => Model.Name;
    public string Course => Model.CourseName;
    public string DueText { get; init; } = "";
    public Brush DueBrush { get; init; } = null!;
    public Visibility SubmittedVisibility => Model.Submitted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnseenVisibility => Model.UnseenChange ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class AssignmentsPage : Page
{
    static App A => App.Current;
    static readonly System.Globalization.CultureInfo De = System.Globalization.CultureInfo.GetCultureInfo("de-AT");
    readonly List<ListView> _lists = [];
    Assignment? _current;
    long? _showOnLoad;

    public AssignmentsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            if (_showOnLoad is { } id) ShowAssignment(id);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is long id) _showOnLoad = id;
    }

    // ---------- list

    public static string RelativeDue(DateTimeOffset? due, DateTimeOffset now)
    {
        if (due is null) return "no due date";
        var d = due.Value - now;
        var abs = d.Duration();
        string span = abs.TotalMinutes < 60 ? $"{Math.Max(1, (int)abs.TotalMinutes)} min"
            : abs.TotalHours < 48 ? $"{(int)abs.TotalHours} h"
            : $"{(int)abs.TotalDays} days";
        return d >= TimeSpan.Zero ? $"in {span}" : $"{span} ago";
    }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Due-time colour shared by the list and the detail pane: red = overdue / within 24 h, amber = within 3 days, else muted.</summary>
    static Brush DueBrush(Assignment a, DateTimeOffset now)
    {
        if (a.Submitted || a.DueAt is not { } due) return Res("TextFillColorSecondaryBrush");
        if (due - now <= TimeSpan.FromHours(24)) return Res("SystemFillColorCriticalBrush");
        if (due - now <= TimeSpan.FromDays(3)) return Res("SystemFillColorCautionBrush");
        return Res("TextFillColorSecondaryBrush");
    }

    // Collapsed groups survive reloads; Overdue and Submitted start collapsed.
    readonly HashSet<string> _collapsed = ["Overdue", "Submitted"];

    public void Reload()
    {
        var now = DateTimeOffset.Now;
        var filter = Math.Max(0, FilterBar.Items.IndexOf(FilterBar.SelectedItem));
        var everything = A.Db.GetAssignments();
        var all = everything
            .Where(a => filter == 0 || (filter == 1 ? !a.Submitted : a.Submitted))
            .OrderBy(a => a.DueAt ?? DateTimeOffset.MaxValue).ToList();

        var todayEnd = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        var weekEnd = todayEnd.AddDays(6);
        var groups = new (string Name, Func<Assignment, bool> Pred)[]
        {
            ("Overdue", a => !a.Submitted && a.DueAt is { } d && d < now),
            ("Today", a => !a.Submitted && a.DueAt is { } d && d >= now && d < todayEnd),
            ("This week", a => !a.Submitted && a.DueAt is { } d && d >= todayEnd && d < weekEnd),
            ("Later", a => !a.Submitted && (a.DueAt is not { } d || d >= weekEnd)),
            ("Submitted", a => a.Submitted),
        };

        // One quiet line: what needs attention right now.
        var open = everything.Where(a => !a.Submitted).ToList();
        var parts = new List<string>();
        var overdue = open.Count(a => a.DueAt is { } d && d < now);
        var today = open.Count(a => a.DueAt is { } d && d >= now && d < todayEnd);
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (today > 0) parts.Add($"{today} due today");
        parts.Add($"{open.Count} open");
        SummaryText.Text = string.Join("  ·  ", parts);

        GroupsPanel.Children.Clear();
        _lists.Clear();
        foreach (var (name, pred) in groups)
        {
            var items = all.Where(pred).ToList();
            if (items.Count == 0) continue;
            var rows = items.Select(a => new AssignmentRow { Model = a, DueText = RelativeDue(a.DueAt, now), DueBrush = DueBrush(a, now) }).ToList();
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                ItemTemplate = (DataTemplate)Resources["RowTemplate"],
                ItemContainerStyle = (Style)Resources["RowContainerStyle"],
                ItemsSource = rows,
                Visibility = _collapsed.Contains(name) ? Visibility.Collapsed : Visibility.Visible,
            };
            list.SelectionChanged += List_SelectionChanged;
            _lists.Add(list);
            GroupsPanel.Children.Add(GroupHeader(name, items.Count, list));
            GroupsPanel.Children.Add(list);
        }
        SelectCurrent();
        var last = A.Settings.LastSync;
        LastSyncText.Text = last is null ? "never synced" : $"last sync {last.Value.ToLocalTime():g}";
    }

    FrameworkElement GroupHeader(string name, int count, ListView list)
    {
        var chevron = new FontIcon { Glyph = list.Visibility == Visibility.Visible ? "" : "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                                     Foreground = Res("TextFillColorSecondaryBrush") };
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var counter = new TextBlock { Text = count.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") };
        Grid.SetColumn(counter, 1);
        content.Children.Add(counter);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(chevron);

        var header = new Button
        {
            Content = content, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 10, 0, 0),
        };
        header.Click += (_, _) =>
        {
            var show = list.Visibility != Visibility.Visible;
            list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = show ? "" : "";
            if (show) _collapsed.Remove(name); else _collapsed.Add(name);
        };
        return header;
    }

    bool _selecting;

    /// <summary>Highlights the open assignment in whichever group holds it (and expands that group).</summary>
    void SelectCurrent()
    {
        _selecting = true;
        foreach (var list in _lists)
        {
            var row = _current is { } c ? ((List<AssignmentRow>)list.ItemsSource).FirstOrDefault(r => r.Model.Id == c.Id) : null;
            list.SelectedItem = row;
            if (row is not null && list.Visibility != Visibility.Visible)
            {
                var idx = GroupsPanel.Children.IndexOf(list) - 1;   // the header sits right before its list
                list.Visibility = Visibility.Visible;
                if (idx >= 0 && GroupsPanel.Children[idx] is Button { Content: Grid g })
                {
                    if (g.Children.OfType<FontIcon>().FirstOrDefault() is { } chev) chev.Glyph = "";
                    if (g.Children.OfType<TextBlock>().FirstOrDefault() is { } t) _collapsed.Remove(t.Text);
                }
            }
        }
        _selecting = false;
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || sender is not ListView lv || lv.SelectedItem is not AssignmentRow row) return;
        _selecting = true;
        foreach (var other in _lists.Where(l => l != lv)) other.SelectedItem = null;
        _selecting = false;
        ShowDetail(row.Model.Id);
    }

    void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!IsLoaded) return;
        // Each tab starts from its own defaults: Submitted is only open by default on the Submitted tab.
        bool submittedTab = FilterBar.Items.IndexOf(FilterBar.SelectedItem) == 2;
        _collapsed.Clear();
        _collapsed.Add("Overdue");
        if (!submittedTab) _collapsed.Add("Submitted");
        Reload();
    }

    /// <summary>Deep link (toast click).</summary>
    public void ShowAssignment(long id)
    {
        if (!IsLoaded) { _showOnLoad = id; return; }
        Reload();
        ShowDetail(id);
    }

    // ---------- detail

    void ShowDetail(long id)
    {
        var a = A.Db.GetAssignment(id);
        if (a is null) return;
        _current = a;
        EmptyHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        var now = DateTimeOffset.Now;

        DetailTitle.Text = a.Name;
        DetailCourse.Text = a.CourseName;
        if (a.DueAt is { } d)
        {
            var local = d.ToLocalTime();
            DateDow.Text = local.ToString("ddd", De).TrimEnd('.').ToUpperInvariant();
            DateDay.Text = local.Day.ToString();
            DateMonth.Text = local.ToString("MMM yyyy", De);
            DetailDue.Text = $"Due {local.ToString("dddd, d MMMM yyyy", De)} at {local:HH:mm}";
            DueRelative.Text = "· " + RelativeDue(d, now);
            DueRelative.Foreground = DueBrush(a, now);
        }
        else
        {
            DateDow.Text = "";
            DateDay.Text = "—";
            DateMonth.Text = "no due date";
            DetailDue.Text = "No due date";
            DueRelative.Text = "";
        }
        StatusIcon.Glyph = a.Submitted ? "" : "";
        StatusText.Text = a.Submitted ? "Submitted" : "Not submitted";
        StatusIcon.Foreground = Res(a.Submitted ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        AttachmentsText.Text = $"{a.AttachmentCount} attachment{(a.AttachmentCount == 1 ? "" : "s")}";

        var hasGrade = a.Grade is { Length: > 0 } || a.Feedback is { Length: > 0 };
        GradeSection.Visibility = hasGrade ? Visibility.Visible : Visibility.Collapsed;
        DetailGrade.Text = a.Grade is { Length: > 0 } g ? g : "—";
        DetailFeedback.Text = a.Feedback ?? "";
        DetailFeedback.Visibility = string.IsNullOrWhiteSpace(a.Feedback) ? Visibility.Collapsed : Visibility.Visible;
        DetailDescription.Text = DocxGenerator.HtmlToPlainText(a.IntroHtml) is { Length: > 0 } t ? t : "(no description)";
        HistoryExpander.IsExpanded = false;
        HistoryList.ItemsSource = null;
        DiffPanel.Children.Clear();

        if (a.UnseenChange)
        {
            A.Db.ClearUnseen(a.Id);  // opening an assignment clears the dot
            Reload();
        }
        else SelectCurrent();
    }

    async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } a)
            await Windows.System.Launcher.LaunchUriAsync(new Uri(A.Client.AssignmentUrl(a.Cmid)));
    }

    async void DocxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } a) return;
        try
        {
            var template = A.Settings.TemplatePath;
            if (!File.Exists(template))
            {
                if (template == Paths.DefaultTemplate) TemplateBuilder.EnsureDefaultTemplate();
                else { Show("Template not found: " + template, InfoBarSeverity.Error); return; }
            }
            var bytes = DocxGenerator.Generate(template, DocxGenerator.BuildValues(a, A.Settings, A.Client.AssignmentUrl(a.Cmid)));

            // Opens in the configured folder (Settings → Word template), filename prefilled.
            var folder = A.Settings.DocxFolder;
            var startDir = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = NativeSaveDialog.Show(A.Window!.Hwnd, startDir, DocxGenerator.SuggestFileName(a));
            if (path is null) return;
            await File.WriteAllBytesAsync(path, bytes);
            Show("Saved " + Path.GetFileName(path), InfoBarSeverity.Success, await StorageFile.GetFileFromPathAsync(path));
        }
        catch (Exception ex) { Show("Could not generate .docx: " + ex.Message, InfoBarSeverity.Error); }
    }

    // ---------- history / diff

    record SnapView(Snapshot Snap, string Label);

    void HistoryExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_current is null) return;
        var snaps = A.Db.GetSnapshots(_current.Id);
        HistoryList.ItemsSource = snaps.Select(s => new SnapView(s, $"{s.TakenAt.ToLocalTime():g} — {s.Kinds}")).ToList();
        HistoryList.DisplayMemberPath = nameof(SnapView.Label);
        if (snaps.Count > 0) HistoryList.SelectedIndex = 0;
    }

    void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DiffPanel.Children.Clear();
        if (HistoryList.SelectedItem is not SnapView sv || _current is null) return;
        var snaps = A.Db.GetSnapshots(_current.Id);
        var idx = snaps.FindIndex(s => s.Id == sv.Snap.Id);
        var older = idx + 1 < snaps.Count ? snaps[idx + 1] : null;
        var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(SnapshotText(older), SnapshotText(sv.Snap), ignoreWhitespace: false);

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
        if (DiffPanel.Children.Count == 0)
            DiffPanel.Children.Add(new TextBlock { Text = "No differences.", Opacity = 0.7 });
    }

    static string SnapshotText(Snapshot? s)
    {
        if (s is null) return "";
        using var doc = JsonDocument.Parse(s.Json);
        var r = doc.RootElement;
        string Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var due = r.TryGetProperty("Due", out var dv) && dv.GetInt64() > 0
            ? DateTimeOffset.FromUnixTimeSeconds(dv.GetInt64()).ToLocalTime().ToString("g") : "—";
        var lines = new List<string>
        {
            "Title: " + Str("Name"),
            "Due: " + due,
            "Submitted: " + (r.TryGetProperty("Submitted", out var sub) && sub.ValueKind == JsonValueKind.True ? "yes" : "no"),
            "Grade: " + (Str("Grade") is { Length: > 0 } g ? g : "—"),
            "Feedback: " + (Str("Feedback") is { Length: > 0 } f ? f : "—"),
            "Attachments:",
        };
        if (r.TryGetProperty("Attachments", out var atts))
            foreach (var a in atts.EnumerateArray())
                lines.Add($"  {a.GetProperty("Filename").GetString()} ({a.GetProperty("Size").GetInt64()} B)");
        lines.Add("Description:");
        lines.AddRange(DocxGenerator.HtmlToPlainText(Str("IntroHtml")).Split('\n'));
        return string.Join("\n", lines);
    }

    // ---------- sync

    async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        Busy.IsActive = true;
        Info.IsOpen = false;
        try
        {
            var result = await Task.Run(() => A.Sync.RunAsync());
            if (result.LoginRequired) Show("Moodle login required — go to Settings → Login.", InfoBarSeverity.Warning);
            else if (result.Error is not null) Show("Sync failed: " + result.Error, InfoBarSeverity.Error);
            else Show($"Synced {result.Assignments} assignments, {result.Changed} changed.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Show("Sync failed: " + ex.Message, InfoBarSeverity.Error); }
        finally
        {
            Busy.IsActive = false;
            SyncButton.IsEnabled = true;
            Reload();
            if (_current is { } c) ShowDetail(c.Id);
        }
    }

    void Show(string msg, InfoBarSeverity sev, StorageFile? openable = null)
    {
        Info.ActionButton = null;
        if (openable is not null)
        {
            var btn = new Button { Content = "Open document" };
            btn.Click += async (_, _) => await Windows.System.Launcher.LaunchFileAsync(openable);
            Info.ActionButton = btn;
        }
        Info.Message = msg;
        Info.Severity = sev;
        Info.IsOpen = true;
    }
}
