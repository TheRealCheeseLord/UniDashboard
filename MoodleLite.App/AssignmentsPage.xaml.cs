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
using MoodleLite.Core;
using MoodleLite.Core.Docx;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MoodleLite.App;

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

    Brush DueBrush(Assignment a, DateTimeOffset now)
    {
        string key = "TextFillColorSecondaryBrush";
        if (!a.Submitted && a.DueAt is { } due)
        {
            if (due < now) key = "SystemFillColorCriticalBrush";
            else if (due - now <= TimeSpan.FromHours(24)) key = "SystemFillColorCriticalBrush";
            else if (due - now <= TimeSpan.FromDays(3)) key = "SystemFillColorCautionBrush";
        }
        return (Brush)Application.Current.Resources[key];
    }

    public void Reload()
    {
        var now = DateTimeOffset.Now;
        var filter = FilterBox.SelectedIndex;
        var all = A.Db.GetAssignments()
            .Where(a => filter == 0 || (filter == 1 ? !a.Submitted : a.Submitted))
            .OrderBy(a => a.DueAt ?? DateTimeOffset.MaxValue).ToList();

        var todayEnd = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        var weekEnd = todayEnd.AddDays(6);
        var groups = new (string Name, bool Expanded, Func<Assignment, bool> Pred)[]
        {
            ("Overdue", true, a => !a.Submitted && a.DueAt is { } d && d < now),
            ("Today", true, a => !a.Submitted && a.DueAt is { } d && d >= now && d < todayEnd),
            ("This week", true, a => !a.Submitted && a.DueAt is { } d && d >= todayEnd && d < weekEnd),
            ("Later", true, a => !a.Submitted && (a.DueAt is not { } d || d >= weekEnd)),
            ("Submitted", false, a => a.Submitted),
        };

        var selectedId = _current?.Id;
        GroupsPanel.Children.Clear();
        _lists.Clear();
        foreach (var (name, expanded, pred) in groups)
        {
            var items = all.Where(pred).ToList();
            if (items.Count == 0) continue;
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                ItemTemplate = (DataTemplate)Resources["RowTemplate"],
                ItemsSource = items.Select(a => new AssignmentRow
                {
                    Model = a, DueText = RelativeDue(a.DueAt, now), DueBrush = DueBrush(a, now),
                }).ToList(),
            };
            list.SelectionChanged += List_SelectionChanged;
            _lists.Add(list);
            GroupsPanel.Children.Add(new Expander
            {
                Header = $"{name} ({items.Count})", IsExpanded = expanded, Content = list,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
            if (selectedId is { } sid)
                list.SelectedItem = ((List<AssignmentRow>)list.ItemsSource).FirstOrDefault(r => r.Model.Id == sid);
        }
        var last = A.Settings.LastSync;
        LastSyncText.Text = last is null ? "never synced" : $"last sync {last.Value.ToLocalTime():g}";
    }

    bool _selecting;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || sender is not ListView lv || lv.SelectedItem is not AssignmentRow row) return;
        _selecting = true;
        foreach (var other in _lists.Where(l => l != lv)) other.SelectedItem = null;
        _selecting = false;
        ShowDetail(row.Model.Id);
    }

    void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Reload(); }

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
        DetailDue.Text = a.DueAt is { } d ? $"Due: {d.ToLocalTime():dddd, d MMMM yyyy HH:mm} ({RelativeDue(d, now)})" : "No due date";
        DetailStatus.Text = a.Submitted ? "Status: ✅ Submitted" : "Status: Not submitted";
        DetailGrade.Text = a.Grade is null && a.Feedback is null ? "" : $"Grade: {a.Grade ?? "—"}" + (a.Feedback is null ? "" : $"\nFeedback: {a.Feedback}");
        DetailGrade.Visibility = DetailGrade.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailAttachments.Text = $"📎 {a.AttachmentCount} attachment{(a.AttachmentCount == 1 ? "" : "s")} (download in Moodle)";
        DetailDescription.Text = DocxGenerator.HtmlToPlainText(a.IntroHtml) is { Length: > 0 } t ? t : "(no description)";
        HistoryExpander.IsExpanded = false;
        HistoryList.ItemsSource = null;
        DiffPanel.Children.Clear();

        if (a.UnseenChange)
        {
            A.Db.ClearUnseen(a.Id);  // opening an assignment clears the dot
            Reload();
        }
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

            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads };
            InitializeWithWindow.Initialize(picker, A.Window!.Hwnd);
            picker.FileTypeChoices.Add("Word document", [".docx"]);
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(DocxGenerator.SuggestFileName(a));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteBytesAsync(file, bytes);
            Show("Saved " + file.Name, InfoBarSeverity.Success, file);
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
