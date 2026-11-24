using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniDashboard.Core;
using UniDashboard.Core.Auth;
using UniDashboard.Core.Docx;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace UniDashboard.App;

public sealed partial class SettingsPage : Page
{
    static App A => App.Current;
    static AppSettings S => A.Settings;
    /// <summary>Optional tab requested via "--settings=notify" etc.</summary>
    public static string? RequestedTab { get; set; }
    bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadValues();
            if (RequestedTab is { } t) { foreach (SelectorBarItem it in Tabs.Items) if ((string?)it.Tag == t) Tabs.SelectedItem = it; RequestedTab = null; }
            if (A.Window?.ConsumePendingLogin() == true) await LoginAsync();
        };
    }

    void LoadValues()
    {
        _loading = true;
        BaseUrlBox.Text = S.MoodleBaseUrl;
        LoginStatus.Text = A.Auth.CurrentToken() is null ? "Not logged in" : "● Logged in";
        if (CredentialStore.Load() is { } c) UserBox.Text = c.User;
        IntervalBox.Value = S.SyncIntervalMinutes;
        ReminderBox.Value = S.ReminderHours;
        AgentSwitch.IsOn = AgentScheduler.IsRegistered();
        DemoSwitch.IsOn = S.ShowDemoEvents;
        MovedSwitch.IsOn = S.ShowMovedRemoved;
        NameBox.Text = S.StudentName;
        MatrikelBox.Text = S.Matrikelnummer;
        TemplateBox.Text = S.TemplatePath;
        DocxFolderBox.Text = S.DocxFolder;
        BuildCourses();
        BuildNotifyToggles();
        _loading = false;
    }

    void BuildCourses()
    {
        CoursePanel.Children.Clear();
        var courses = A.Db.GetCourses();
        if (courses.Count == 0)
            CoursePanel.Children.Add(new TextBlock { Text = "No courses yet — log in and refresh.", Opacity = 0.7 });
        foreach (var c in courses)
        {
            var cb = new CheckBox { Content = c.Name, IsChecked = c.Enabled, Tag = c.Id };
            cb.Click += (_, _) => A.Db.SetCourseEnabled(c.Id, cb.IsChecked == true);
            CoursePanel.Children.Add(cb);
        }
    }

    void BuildNotifyToggles()
    {
        foreach (var panel in new[] { NotifyAssignments, NotifyCourses, NotifyExams, NotifySystem }) panel.Children.Clear();

        void Add(StackPanel panel, string label, bool on, Action<bool> set, string? hint = null)
        {
            var cb = new CheckBox { Content = hint is null ? label : $"{label}  —  {hint}", IsChecked = on, MinWidth = 0 };
            cb.Click += (_, _) => { if (!_loading) set(cb.IsChecked == true); };
            panel.Children.Add(cb);
        }

        // Assignments
        Add(NotifyAssignments, "New assignment", S.Notify(ChangeKind.New), v => S.SetNotify(ChangeKind.New, v));
        Add(NotifyAssignments, "Due date changed", S.Notify(ChangeKind.DueDateChanged), v => S.SetNotify(ChangeKind.DueDateChanged, v));
        Add(NotifyAssignments, "Description changed", S.Notify(ChangeKind.DescriptionChanged), v => S.SetNotify(ChangeKind.DescriptionChanged, v));
        Add(NotifyAssignments, "Grade or feedback", S.Notify(ChangeKind.GradeOrFeedback), v => S.SetNotify(ChangeKind.GradeOrFeedback, v));
        Add(NotifyAssignments, "Attachments changed", S.Notify(ChangeKind.AttachmentsChanged), v => S.SetNotify(ChangeKind.AttachmentsChanged, v), "off by default");
        Add(NotifyAssignments, "Submission status changed", S.Notify(ChangeKind.Submitted), v => S.SetNotify(ChangeKind.Submitted, v), "off by default");
        Add(NotifyAssignments, "Deadline reminder", S.NotifyDueReminder, v => S.NotifyDueReminder = v, "see “Sync” for how early");

        // Courses, lectures and exams (the app's own list, split by what the entry is about)
        foreach (var (kind, label, _) in UniDashboard.Core.Univie.CourseKinds.All)
        {
            var k = kind;
            var panel = k.StartsWith("Exam", StringComparison.Ordinal) ? NotifyExams : NotifyCourses;
            Add(panel, label, S.NotifyCourse(k), v => S.SetNotifyCourse(k, v), k == UniDashboard.Core.Univie.CourseKinds.Info ? "off by default" : null);
        }

        // App
        Add(NotifySystem, "Login required again", S.NotifyLoginRequired, v => S.NotifyLoginRequired = v, "when the background agent cannot sign in on its own");
    }

    // ---- tabs
    void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = Tabs.SelectedItem?.Tag as string;
        TabAccounts.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        TabSync.Visibility = tag == "sync" ? Visibility.Visible : Visibility.Collapsed;
        TabNotify.Visibility = tag == "notify" ? Visibility.Visible : Visibility.Collapsed;
        TabCalendar.Visibility = tag == "calendar" ? Visibility.Visible : Visibility.Collapsed;
        TabDocuments.Visibility = tag == "documents" ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenExport_Click(object sender, RoutedEventArgs e) => A.Window?.HandleCommandLine("--export");

    void SetAllCourses(bool enabled)
    {
        foreach (var c in A.Db.GetCourses()) A.Db.SetCourseEnabled(c.Id, enabled);
        BuildCourses();
    }
    void SelectAllCourses_Click(object sender, RoutedEventArgs e) => SetAllCourses(true);
    void SelectNoCourses_Click(object sender, RoutedEventArgs e) => SetAllCourses(false);

    // ---- login
    public async Task LoginAsync()
    {
        LoginButton.IsEnabled = false;
        Busy.IsActive = true;
        LoginStatus.Text = "Waiting for you to log in in the browser window…";
        try
        {
            await Task.Run(() => A.Auth.LoginAsync(headless: false));
            S.UserId = 0;
            LoginStatus.Text = "● Logged in";
            Show("Login successful.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            LoginStatus.Text = "Login failed";
            Show(ex.Message, InfoBarSeverity.Error);
        }
        finally { Busy.IsActive = false; LoginButton.IsEnabled = true; }
    }

    async void LoginButton_Click(object sender, RoutedEventArgs e) => await LoginAsync();

    void SaveCreds_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text) || PassBox.Password.Length == 0)
        { Show("Enter username and password.", InfoBarSeverity.Warning); return; }
        CredentialStore.Save(UserBox.Text.Trim(), PassBox.Password);
        PassBox.Password = "";
        Show("Credentials stored in Windows Credential Manager.", InfoBarSeverity.Success);
    }

    void RemoveCreds_Click(object sender, RoutedEventArgs e)
    {
        CredentialStore.Delete();
        UserBox.Text = "";
        Show("Credentials removed.", InfoBarSeverity.Informational);
    }

    async void RefreshCourses_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var courses = await Task.Run(() => A.Client.GetCoursesAsync());
            foreach (var c in courses) A.Db.UpsertCourse(new(c.Id, c.FullName, c.ShortName, true));
            BuildCourses();
        }
        catch (Exception ex) { Show("Could not load courses: " + ex.Message, InfoBarSeverity.Error); }
    }

    // ---- simple fields
    void BaseUrlBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) S.MoodleBaseUrl = BaseUrlBox.Text; }
    void NameBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) S.StudentName = NameBox.Text; }
    void MatrikelBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) S.Matrikelnummer = MatrikelBox.Text; }
    void DocxFolderBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) S.DocxFolder = DocxFolderBox.Text; }

    async void BrowseDocxFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, A.Window!.Hwnd);
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFolderAsync() is { } f)
        {
            DocxFolderBox.Text = f.Path;
            S.DocxFolder = f.Path;
        }
    }

    void TemplateBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) S.TemplatePath = TemplateBox.Text; }

    void ReminderBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    { if (!_loading && !double.IsNaN(args.NewValue)) S.ReminderHours = (int)args.NewValue; }

    void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        S.SyncIntervalMinutes = (int)args.NewValue;
        if (AgentScheduler.IsRegistered()) RegisterAgent();   // apply the new interval
    }

    // ---- agent
    static string AgentExe => Path.Combine(AppContext.BaseDirectory, "UniDashboard.Agent.exe");

    void RegisterAgent() => AgentScheduler.Register(AgentExe, S.SyncIntervalMinutes);

    void AgentSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (AgentSwitch.IsOn)
            {
                if (!File.Exists(AgentExe)) { Show("UniDashboard.Agent.exe not found next to the app.", InfoBarSeverity.Error); _loading = true; AgentSwitch.IsOn = false; _loading = false; return; }
                RegisterAgent();
                S.AgentEnabled = true;
            }
            else { AgentScheduler.Unregister(); S.AgentEnabled = false; }
        }
        catch (Exception ex) { Show("Task Scheduler error: " + ex.Message, InfoBarSeverity.Error); }
    }

    void MovedSwitch_Toggled(object sender, RoutedEventArgs e) { if (!_loading) S.ShowMovedRemoved = MovedSwitch.IsOn; }

    void DemoSwitch_Toggled(object sender, RoutedEventArgs e) { if (!_loading) S.ShowDemoEvents = DemoSwitch.IsOn; }

    // ---- template
    async void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, A.Window!.Hwnd);
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".dotx");
        if (await picker.PickSingleFileAsync() is { } f)
        {
            TemplateBox.Text = f.Path;
            S.TemplatePath = f.Path;
        }
    }

    void ResetTemplate_Click(object sender, RoutedEventArgs e)
    {
        TemplateBuilder.Build(Paths.DefaultTemplate);
        S.TemplatePath = Paths.DefaultTemplate;
        TemplateBox.Text = Paths.DefaultTemplate;
        Show("Default template restored.", InfoBarSeverity.Success);
    }

    void Show(string msg, InfoBarSeverity sev) { Info.Message = msg; Info.Severity = sev; Info.IsOpen = true; }
}
