using MoodleDashboard.Core;
using MoodleDashboard.Core.Api;
using MoodleDashboard.Core.Auth;

// Smoke test. Flags: --fresh-login   forget token + saved web session, then log in headless and time it
//                    --reuse         force a login attempt via the saved session only (timing)
var db = new Database();
var settings = new AppSettings(db);
var tokens = new TokenStore();
var auth = new AuthService(settings, tokens);

if (args.Contains("--uspace"))
{
    var us = new MoodleDashboard.Core.Uspace.UspaceClient(settings);
    if (args.Contains("--fresh-login")) us.ForgetSession();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var list = await us.GetEnrolmentsAsync(headless: true);
    Console.WriteLine($"u:space via {us.LastMethod} in {sw.Elapsed.TotalSeconds:F1}s — {list.Count} enrolments");
    foreach (var e in list)
        Console.WriteLine($"{e.Key,-18} {e.Status,-10} {e.Type,-4} {e.Sessions.Count,2} sessions  moodle={e.MoodleCourseId?.ToString() ?? "-"}  {e.Title}");
    return;
}

if (args.Contains("--univie-sync"))
{
    using var h = MoodleDashboard.Core.Ufind.UfindClient.CreateHttpClient();
    var svc = new MoodleDashboard.Core.Univie.UnivieSyncService(db, settings, new MoodleDashboard.Core.Uspace.UspaceClient(settings),
        new MoodleDashboard.Core.Ufind.UfindClient(h), new ConsoleNotifier(), headless: true, Console.WriteLine);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = await svc.RunAsync();
    Console.WriteLine($"univie sync: {r.Enrolments} enrolments, {r.Changed} changed, {r.Notified} toasts, error={r.Error ?? "none"} in {sw.Elapsed.TotalSeconds:F1}s");
    return;
}

if (args.Contains("--register-agent"))   // (re)create the scheduled task, replacing the pre-rename one
{
    var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoodleDashboard", "bin", "MoodleDashboard.Agent.exe");
    AgentScheduler.Register(exe, settings.SyncIntervalMinutes);
    Console.WriteLine("Task registered for " + exe);
    return;
}

if (args.Contains("--fresh-login") || args.Contains("--reuse"))
{
    if (args.Contains("--fresh-login")) auth.ForgetSession();
    var t = await auth.LoginAsync(headless: true);
    Console.WriteLine($"login via {auth.LastLogin!.Method} in {auth.LastLogin.Elapsed.TotalSeconds:F1}s (token length {t.Length})");
}

using var http = new HttpClient();
var client = new MoodleClient(http, settings, auth, headlessReauth: true);
Console.WriteLine($"User id: {await client.GetUserIdAsync()}");
var courses = await client.GetCoursesAsync();
var assigns = await client.GetAssignmentsAsync(courses.Select(c => c.Id));
Console.WriteLine($"{courses.Count} courses, {assigns.Count} assignments");

class ConsoleNotifier : MoodleDashboard.Core.Sync.INotifier
{
    public void Send(MoodleDashboard.Core.PendingNotification n) => Console.WriteLine($"[toast] {n.Title}: {n.Body}");
}
