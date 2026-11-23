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

            await GoogleAutoSyncAsync(db, settings, http, healthy: result.Error is null, Log);

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

    /// <summary>Background Google sync: only when enabled (after one manual apply). Creates/updates/deletes within the safety guards; never the clean-up.</summary>
    static async Task GoogleAutoSyncAsync(Database db, AppSettings settings, HttpClient http, bool healthy, Action<string> log)
    {
        if (!settings.GoogleAutoSync || !settings.GoogleApplied || string.IsNullOrEmpty(settings.GoogleTargetCalendarId)) return;
        var creds = new MoodleDashboard.Core.Google.GoogleCredentialStore();
        var auth = new MoodleDashboard.Core.Google.GoogleAuth(http, creds);
        if (!auth.IsConnected) return;

        string? problem = null;
        try
        {
            var api = new MoodleDashboard.Core.Google.GoogleCalendarClient(http, (force, ct) => auth.GetAccessTokenAsync(force, ct));
            var r = await new MoodleDashboard.Core.Google.GoogleSyncService(db, settings, api, null, log).SyncAsync(healthy);
            if (r.DeletesBlocked) problem = "Google Calendar: many deletions were held back — open MoodleDashboard → Export to review.";
            else if (r.Errors.Count > 0) problem = $"Google Calendar sync finished with {r.Errors.Count} problem(s).";
        }
        catch (MoodleDashboard.Core.Google.GoogleAuthRequiredException e) { log("google: " + e.Message); problem = "Google Calendar needs reconnecting — open MoodleDashboard → Export."; }
        catch (Exception e) when (e is HttpRequestException or MoodleDashboard.Core.Google.GoogleApiException or InvalidOperationException or TaskCanceledException or TimeoutException)
        { log("google sync failed: " + e.Message); problem = "Google Calendar sync failed: " + e.Message; }

        if (problem is not null && !db.WasSent(0, "GoogleSync", DateTime.Today.ToString("yyyyMMdd")))
        {
            ToastNotifier.Show("Google Calendar", problem, "login=1");
            db.MarkSent(0, "GoogleSync", DateTime.Today.ToString("yyyyMMdd"), DateTimeOffset.UtcNow);
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
