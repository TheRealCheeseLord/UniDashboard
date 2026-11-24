using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Export;

namespace MoodleDashboard.App;

public sealed partial class ExportPage : Page
{
    static App A => App.Current;
    /// <summary>Optional tab requested via "--export=google".</summary>
    public static string? RequestedTab { get; set; }
    bool _loading = true;

    public ExportPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            SessionsBox.IsChecked = A.Settings.ExportSessions;
            ExamsBox.IsChecked = A.Settings.ExportExams;
            FutureBox.IsChecked = A.Settings.ExportFutureOnly;
            foreach (ComboBoxItem it in AlarmBox.Items)
                if ((string)it.Tag == A.Settings.ExportAlarmMinutes.ToString()) AlarmBox.SelectedItem = it;
            _loading = false;
            UpdateCount();
            BuildGoogleSection();
            if (RequestedTab is { } t) { foreach (SelectorBarItem it in Tabs.Items) if ((string?)it.Tag == t) Tabs.SelectedItem = it; RequestedTab = null; }
        };
    }

    void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var google = (Tabs.SelectedItem?.Tag as string) == "google";
        TabIcs.Visibility = google ? Visibility.Collapsed : Visibility.Visible;
        GooglePanel.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
    }

    ExportOptions Options() => new()
    {
        Sessions = SessionsBox.IsChecked == true,
        RegisteredExams = ExamsBox.IsChecked == true,
        FromTodayOnly = FutureBox.IsChecked == true,
    };

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        A.Settings.ExportSessions = SessionsBox.IsChecked == true;
        A.Settings.ExportExams = ExamsBox.IsChecked == true;
        A.Settings.ExportFutureOnly = FutureBox.IsChecked == true;
        UpdateCount();
    }

    void Alarm_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AlarmBox.SelectedItem is not ComboBoxItem it) return;
        A.Settings.ExportAlarmMinutes = int.Parse((string)it.Tag);
    }

    void UpdateCount()
    {
        var items = ExportBuilder.Build(A.Db, Options(), DateTimeOffset.Now);
var sessions = items.Count(i => i.Kind == ExportKind.Session);
        var exams = items.Count(i => i.Kind == ExportKind.Exam);
        CountSessions.Text = $"{sessions} session{(sessions == 1 ? "" : "s")}";
        CountExams.Text = $"{exams} exam{(exams == 1 ? "" : "s")}";
    }

    void Show(string msg, InfoBarSeverity sev, string? openPath = null)
    {
        Info.ActionButton = null;
        if (openPath is not null)
        {
            var btn = new Button { Content = "Show in folder" };
            btn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{openPath}\"") { UseShellExecute = true });
            Info.ActionButton = btn;
        }
        Info.Message = msg;
        Info.Severity = sev;
        Info.IsOpen = true;
    }

    void ExportIcs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var items = ExportBuilder.Build(A.Db, Options(), now);
            if (items.Count == 0) { Show("Nothing to export with the current selection.", InfoBarSeverity.Warning); return; }

            var (seqs, save) = IcsWriter.NextSequences(items, A.Db.GetExportState());
            var ics = IcsWriter.Write(items, i => seqs[i.Key], now, A.Settings.ExportAlarmMinutes);

            var dir = A.Settings.ExportFolder;
            var start = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = NativeSaveDialog.Show(A.Window!.Hwnd, start, $"MoodleDashboard-{DateTime.Today:yyyy-MM-dd}.ics", "iCalendar file (*.ics)\0*.ics\0", "ics");
            if (path is null) return;

            File.WriteAllText(path, ics, new UTF8Encoding(false));
            A.Db.SaveExportState(save);                       // only after the file was written
            A.Settings.ExportFolder = Path.GetDirectoryName(path) ?? dir;
            Show($"Exported {items.Count} events to {Path.GetFileName(path)}.", InfoBarSeverity.Success, path);
        }
        catch (Exception ex) { Show("Export failed: " + ex.Message, InfoBarSeverity.Error); }
    }

}
