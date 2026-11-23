using MoodleDashboard.Core;
using MoodleDashboard.Core.Api;
using MoodleDashboard.Core.Auth;

// Smoke test. Flags: --fresh-login   forget token + saved web session, then log in headless and time it
//                    --reuse         force a login attempt via the saved session only (timing)
var db = new Database();
var settings = new AppSettings(db);
var tokens = new TokenStore();
var auth = new AuthService(settings, tokens);

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
