using System.Diagnostics;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Api;
using MoodleDashboard.Core.Auth;
using MoodleDashboard.Core.Docx;
using MoodleDashboard.Core.Sync;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MoodleDashboard.Agent;

/// <summary>Headless: one sync pass + toasts, then exit. Never shows a browser.</summary>
internal static class Program
{
    const string AppExe = "MoodleDashboard.App.exe";

    [STAThread]
    static async Task<int> Main(string[] args)
    {
        void Log(string m) => FileLog.Write("agent", m);

        // Toast click while the agent is not running: Windows launches this exe with the activation args.
        // Forward to the App (deep link to the detail pane) and exit.
        AppNotificationManager.Default.NotificationInvoked += (_, e) => LaunchApp(e.Arguments);
        AppNotificationManager.Default.Register();
        var act = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (act.Kind == ExtendedActivationKind.AppNotification &&
            act.Data is AppNotificationActivatedEventArgs toast)
        {
            LaunchApp(toast.Arguments);
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\MoodleDashboard.Agent", out var created);
        if (!created) { Log("another instance is running; exiting"); return 0; }

        try
        {
            Paths.Root.ToString();
            var db = new Database();
            var settings = new AppSettings(db);
            var auth = new AuthService(settings, new TokenStore());
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var client = new MoodleClient(http, settings, auth, headlessReauth: true);
            var notifier = new ToastNotifier();
            var sync = new SyncService(db, settings, client, notifier, Log);
            sync.Univie = new MoodleDashboard.Core.Univie.UnivieSyncService(db, settings, new MoodleDashboard.Core.Uspace.UspaceClient(settings),
                new MoodleDashboard.Core.Ufind.UfindClient(MoodleDashboard.Core.Ufind.UfindClient.CreateHttpClient()), notifier, headless: true, Log);

            var result = await sync.RunAsync();
            Log($"sync: {result.Assignments} assignments, {result.Changed} changed, {result.Notified} toasts" +
                (result.Error is null ? "" : $", error: {result.Error}"));

            if (result.LoginRequired && settings.NotifyLoginRequired)
            {
                // Dedupe so we don't toast every 30 minutes: once per day.
                var key = DateTime.Today.ToString("yyyyMMdd");
                if (!db.WasSent(0, "LoginRequired", key))
                {
                    ToastNotifier.Show("Moodle login required", "Open MoodleDashboard to sign in again.", "login=1");
                    db.MarkSent(0, "LoginRequired", key, DateTimeOffset.UtcNow);
                }
            }
            return result.Error is null ? 0 : 1;
        }
        catch (Exception e)
        {
            Log("fatal: " + e);
            return 2;
        }
    }

    static void LaunchApp(IDictionary<string, string> toastArgs)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, AppExe);
        if (!File.Exists(exe)) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (toastArgs.TryGetValue("courseKey", out var ck)) psi.ArgumentList.Add("--course=" + ck);
        else if (toastArgs.TryGetValue("assignmentId", out var id)) psi.ArgumentList.Add("--assignment=" + id);
        else if (toastArgs.ContainsKey("login")) psi.ArgumentList.Add("--login");
        Process.Start(psi);
    }
}

internal class ToastNotifier : INotifier
{
    public void Send(PendingNotification n) => Show(n.Title, n.Body, n.CourseKey is null ? "assignmentId=" + n.AssignmentId : "courseKey=" + n.CourseKey);

    public static void Show(string title, string body, string argument)
    {
        var kv = argument.Split('=', 2);
        var toast = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body)
            .AddArgument(kv[0], kv[1])
            .BuildNotification();
        AppNotificationManager.Default.Show(toast);
    }
}
