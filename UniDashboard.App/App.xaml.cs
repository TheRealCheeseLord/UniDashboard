using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UniDashboard.Core;
using UniDashboard.Core.Api;
using UniDashboard.Core.Auth;
using UniDashboard.Core.Docx;
using UniDashboard.Core.Sync;

namespace UniDashboard.App;

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

    // Google Calendar (user-provided OAuth client; tokens DPAPI-encrypted)
    public UniDashboard.Core.Google.GoogleCredentialStore GoogleCreds { get; } = new();
    public UniDashboard.Core.Google.GoogleAuth GoogleAuth { get; }
    public UniDashboard.Core.Google.IGoogleCalendar GoogleApi { get; }
    public UniDashboard.Core.Google.GoogleSyncService GoogleSync { get; }

    // Updates (GitHub releases; verified by SHA-256 before the installer is started)
    public UniDashboard.Core.Update.UpdateService Updater { get; }
    public UniDashboard.Core.Update.UpdateInfo? PendingUpdate { get; private set; }

    /// <summary>Asks GitHub for a newer release; remembers the result in <see cref="PendingUpdate"/>.</summary>
    public async Task<UniDashboard.Core.Update.UpdateInfo?> CheckForUpdateAsync()
    {
        PendingUpdate = await Updater.CheckAsync();
        Settings.LastUpdateCheck = DateTimeOffset.Now;
        return PendingUpdate;
    }

    /// <summary>Downloads + verifies the pending installer, starts it and exits. Returns an error text, or null when the installer took over.</summary>
    public async Task<string?> InstallUpdateAsync()
    {
        if (PendingUpdate is null) return "No update is pending.";
        try
        {
            var setup = await Updater.DownloadAsync(PendingUpdate);
            FileLog.Write("app", $"update: starting installer {setup}");
            UniDashboard.Core.Update.UpdateService.StartInstaller(setup);
            Exit();
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileLog.Write("app", "update failed: " + e);
            return "The update could not be installed: " + e.Message;
        }
    }

    public App()
    {
        InitializeComponent();
        Updater = new UniDashboard.Core.Update.UpdateService(_http, log: m => FileLog.Write("app", m));

        // Never let a stray exception take the whole app down silently: log it (the stack goes to logspp-*.log).
        UnhandledException += (_, e) => { FileLog.Write("app", "UNHANDLED (UI): " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => FileLog.Write("app", "UNHANDLED (domain): " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { FileLog.Write("app", "UNOBSERVED task exception: " + e.Exception); e.SetObserved(); };

        Settings = new AppSettings(Db);
        Auth = new AuthService(Settings, Tokens);
        // The App may show a browser (headed login) when the token expired.
        Client = new MoodleClient(_http, Settings, Auth, headlessReauth: false);
        var notifier = new AppToastNotifier();
        Sync = new SyncService(Db, Settings, Client, notifier, m => FileLog.Write("app", m));
        Sync.Univie = new UniDashboard.Core.Univie.UnivieSyncService(Db, Settings, new UniDashboard.Core.Uspace.UspaceClient(Settings),
            new UniDashboard.Core.Ufind.UfindClient(UniDashboard.Core.Ufind.UfindClient.CreateHttpClient()), notifier, headless: false, m => FileLog.Write("app", m));
        GoogleAuth = new UniDashboard.Core.Google.GoogleAuth(_http, GoogleCreds);
        GoogleApi = new UniDashboard.Core.Google.GoogleCalendarClient(_http, (force, ct) => GoogleAuth.GetAccessTokenAsync(force, ct));
        GoogleSync = new UniDashboard.Core.Google.GoogleSyncService(Db, Settings, GoogleApi, null, m => FileLog.Write("app", m));
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
                Window.HandleCommandLine(e.Arguments.TryGetValue("courseKey", out var ck) ? "--course=" + ck
                    : e.Arguments.TryGetValue("assignmentId", out var id) ? "--assignment=" + id : "");
            });
        AppNotificationManager.Default.Register();

        var instance = AppInstance.GetCurrent();
        instance.Activated += (_, a) => Window.DispatcherQueue.TryEnqueue(() => HandleActivation(a));
        HandleActivation(instance.GetActivatedEventArgs());
        _ = Window.CheckForUpdateOnStartupAsync();
    }

    void HandleActivation(AppActivationArguments a)
    {
        string cmd = a.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l => l.Arguments ?? "",
            AppNotificationActivatedEventArgs t when t.Arguments.TryGetValue("courseKey", out var ck) => "--course=" + ck,
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
                .AddText(n.Title).AddText(n.Body).AddArgument(n.CourseKey is null ? "assignmentId" : "courseKey", n.CourseKey ?? n.AssignmentId.ToString()).BuildNotification());
        }
        catch { /* toasts are best-effort in the App */ }
    }
}
