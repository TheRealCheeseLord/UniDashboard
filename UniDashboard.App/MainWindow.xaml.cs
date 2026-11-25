using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniDashboard.Core;

namespace UniDashboard.App;

public sealed partial class MainWindow : Window
{
    long? _pendingAssignment;
    string? _pendingCourse;
    bool _pendingLogin;

    public MainWindow()
    {
        InitializeComponent();
        Title = "UniDashboard";
        var icon = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);   // title bar + taskbar/Alt-Tab while running
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new(1180, 780));   // size used when un-maximised
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Maximize();
        Nav.SelectedItem = NavItem("assignments");
    }

    NavigationViewItem NavItem(string tag) => Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string?)i.Tag == tag);

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>
    /// Activates the window and really brings it to the front. Plain <c>Activate()</c> can leave a freshly started window behind
    /// others because Windows restricts foreground changes; attaching to the foreground thread's input queue lifts that restriction.
    /// </summary>
    public void BringToFront()
    {
        Activate();
        var h = Hwnd;
        if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            // A synthetic Alt press satisfies Windows' "last input" rule for foreground changes.
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
        // Raise above other windows even if the focus change was refused, then drop the always-on-top flag again.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter pr) { pr.IsAlwaysOnTop = true; pr.IsAlwaysOnTop = false; }
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch { "settings" => typeof(SettingsPage), "calendar" => typeof(CalendarPage), "courses" => typeof(CoursesPage), "planner" => typeof(PlannerPage), "studyplan" => typeof(StudyPlanPage), "export" => typeof(ExportPage), _ => typeof(AssignmentsPage) };
        object? param = tag switch { "assignments" => _pendingAssignment, "courses" => _pendingCourse, _ => null };
        if (tag == "courses") _pendingCourse = null;
        ContentFrame.Navigate(page, param);
    }

    /// <summary>Handles "--assignment=ID" (toast deep link) and "--login".</summary>
    public void HandleCommandLine(string cmd)
    {
        foreach (var part in cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("--assignment=") && long.TryParse(part["--assignment=".Length..], out var id))
            {
                _pendingAssignment = id;
                if (ContentFrame.Content is AssignmentsPage page) page.ShowAssignment(id);
                else if (ReferenceEquals(Nav.SelectedItem, NavItem("assignments"))) ContentFrame.Navigate(typeof(AssignmentsPage), id);
                else Nav.SelectedItem = NavItem("assignments");
            }
            else if (part == "--studyplan" || part.StartsWith("--course=" + UniDashboard.Core.Univie.EnrolmentRecord.SectionPrefix))
            {
                Nav.SelectedItem = NavItem("studyplan");   // curriculum sections have no course page
            }
            else if (part.StartsWith("--course="))
            {
                _pendingCourse = part["--course=".Length..];
                if (ContentFrame.Content is CoursesPage cp) { cp.ShowCourse(_pendingCourse); _pendingCourse = null; }
                else if (ReferenceEquals(Nav.SelectedItem, NavItem("courses"))) ContentFrame.Navigate(typeof(CoursesPage), _pendingCourse);
                else Nav.SelectedItem = NavItem("courses");
            }
            else if (part == "--calendar" || part.StartsWith("--calendar="))
            {
                var arg = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : null;
                if (arg is not null && arg.Contains('@') && DateOnly.TryParse(arg[(arg.IndexOf('@') + 1)..], out var anchor))
                { CalendarPage.RequestedDate = anchor; arg = arg[..arg.IndexOf('@')]; }
                CalendarPage.RequestedView = arg;
                Nav.SelectedItem = NavItem("calendar");
            }
            else if (part == "--planner" || part.StartsWith("--planner="))
            {
                PlannerPage.RequestedTab = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : null;
                Nav.SelectedItem = NavItem("planner");
            }
            else if (part == "--export" || part.StartsWith("--export="))
            {
                ExportPage.RequestedTab = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : null;
                Nav.SelectedItem = NavItem("export");
            }
            else if (part == "--settings" || part.StartsWith("--settings="))
            {
                SettingsPage.RequestedTab = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : null;
                Nav.SelectedItem = NavItem("settings");
            }
            else if (part == "--login")
            {
                Nav.SelectedItem = NavItem("settings");
                _pendingLogin = true;
            }
        }
        if (_pendingLogin && ContentFrame.Content is SettingsPage sp) { _pendingLogin = false; _ = sp.LoginAsync(); }
    }

    // ---------- updates

    /// <summary>On startup: look for a newer release (once a day, if enabled) and offer it in a banner. Never installs on its own.</summary>
    public async Task CheckForUpdateOnStartupAsync()
    {
        var s = App.Current.Settings;
        if (!s.AutoUpdateCheck || DateTimeOffset.Now - s.LastUpdateCheck < TimeSpan.FromHours(24)) return;
        var update = await App.Current.CheckForUpdateAsync();
        if (update is not null) ShowUpdateBanner(update);
    }

    public void ShowUpdateBanner(UniDashboard.Core.Update.UpdateInfo update)
    {
        UpdateBar.Title = $"UniDashboard {update.Version.ToString(3)} is available";
        UpdateBar.Message = $"You have {AppVersion.Text}. Your data stays as it is; the app restarts after the update.";
        UpdateBar.IsOpen = true;
    }

    public void HideUpdateBanner() => UpdateBar.IsOpen = false;

    async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        UpdateInstallButton.IsEnabled = false;
        UpdateBar.Message = "Downloading…";
        var error = await App.Current.InstallUpdateAsync();
        if (error is not null) { UpdateBar.Severity = InfoBarSeverity.Error; UpdateBar.Message = error; UpdateInstallButton.IsEnabled = true; }
    }

    public bool ConsumePendingLogin() { var v = _pendingLogin; _pendingLogin = false; return v; }
}
