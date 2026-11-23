using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using MoodleLite.Core;
using MoodleLite.Core.Api;
using MoodleLite.Core.Auth;
using MoodleLite.Core.Docx;
using MoodleLite.Core.Sync;

namespace MoodleLite.App;

public partial class App : Application
{
    public static App Current => (App)Application.Current;
    public MainWindow? Window { get; private set; }

    public Database Db { get; } = new();
    public AppSettings Settings { get; }
    public TokenStore Tokens { get; } = new();
    public AuthService Auth { get; }
    public MoodleClient Client { get; }
    public SyncService Sync { get; }
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public App()
    {
        InitializeComponent();
        Settings = new AppSettings(Db);
        Auth = new AuthService(Settings, Tokens);
        // The App may show a browser (headed login) when the token expired.
        Client = new MoodleClient(_http, Settings, Auth, headlessReauth: false);
        Sync = new SyncService(Db, Settings, Client, new AppToastNotifier(), m => FileLog.Write("app", m));
        TemplateBuilder.EnsureDefaultTemplate();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();

        AppNotificationManager.Default.NotificationInvoked += (_, e) =>
            Window.DispatcherQueue.TryEnqueue(() =>
            {
                Window.Activate();
                Window.HandleCommandLine(e.Arguments.TryGetValue("assignmentId", out var id) ? "--assignment=" + id : "");
            });
        AppNotificationManager.Default.Register();

        var instance = AppInstance.GetCurrent();
        instance.Activated += (_, a) => Window.DispatcherQueue.TryEnqueue(() => HandleActivation(a));
        HandleActivation(instance.GetActivatedEventArgs());
    }

    void HandleActivation(AppActivationArguments a)
    {
        string cmd = a.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l => l.Arguments ?? "",
            AppNotificationActivatedEventArgs t when t.Arguments.TryGetValue("assignmentId", out var id) => "--assignment=" + id,
            _ => "",
        };
        // Also covers the initial launch with a command line
        if (string.IsNullOrEmpty(cmd)) cmd = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        Window?.Activate();
        Window?.HandleCommandLine(cmd);
    }
}

/// <summary>Toasts raised when the App itself syncs (so changes found by "Sync now" aren't lost to the Agent).</summary>
internal class AppToastNotifier : INotifier
{
    public void Send(PendingNotification n)
    {
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText(n.Title).AddText(n.Body).AddArgument("assignmentId", n.AssignmentId.ToString()).BuildNotification());
        }
        catch { /* toasts are best-effort in the App */ }
    }
}
