using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoodleDashboard.App;

public sealed partial class MainWindow : Window
{
    long? _pendingAssignment;
    bool _pendingLogin;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoodleDashboard";
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new(1180, 780));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ContentFrame.Navigate(tag == "settings" ? typeof(SettingsPage) : typeof(AssignmentsPage), _pendingAssignment);
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
                else Nav.SelectedItem = Nav.MenuItems[0];
            }
            else if (part == "--login")
            {
                Nav.SelectedItem = Nav.MenuItems[1];
                _pendingLogin = true;
            }
        }
        if (_pendingLogin && ContentFrame.Content is SettingsPage sp) { _pendingLogin = false; _ = sp.LoginAsync(); }
    }

    public bool ConsumePendingLogin() { var v = _pendingLogin; _pendingLogin = false; return v; }
}
