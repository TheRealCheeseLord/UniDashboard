using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoodleDashboard.App;

public sealed partial class MainWindow : Window
{
    long? _pendingAssignment;
    string? _pendingCourse;
    bool _pendingLogin;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoodleDashboard";
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new(1180, 780));   // size used when un-maximised
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Maximize();
        Nav.SelectedItem = NavItem("assignments");
    }

    NavigationViewItem NavItem(string tag) => Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string?)i.Tag == tag);

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch { "settings" => typeof(SettingsPage), "calendar" => typeof(CalendarPage), "courses" => typeof(CoursesPage), "planner" => typeof(PlannerPage), _ => typeof(AssignmentsPage) };
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
                else if (Nav.SelectedItem == NavItem("assignments")) ContentFrame.Navigate(typeof(AssignmentsPage), id);
                else Nav.SelectedItem = NavItem("assignments");
            }
            else if (part.StartsWith("--course="))
            {
                _pendingCourse = part["--course=".Length..];
                if (ContentFrame.Content is CoursesPage cp) { cp.ShowCourse(_pendingCourse); _pendingCourse = null; }
                else if (Nav.SelectedItem == NavItem("courses")) ContentFrame.Navigate(typeof(CoursesPage), _pendingCourse);
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
            else if (part == "--planner") Nav.SelectedItem = NavItem("planner");
            else if (part == "--login")
            {
                Nav.SelectedItem = NavItem("settings");
                _pendingLogin = true;
            }
        }
        if (_pendingLogin && ContentFrame.Content is SettingsPage sp) { _pendingLogin = false; _ = sp.LoginAsync(); }
    }

    public bool ConsumePendingLogin() { var v = _pendingLogin; _pendingLogin = false; return v; }
}
