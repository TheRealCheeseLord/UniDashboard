using UniDashboard.Core;
using UniDashboard.Core.Api;
using UniDashboard.Core.Auth;

// Smoke test. Flags: --fresh-login   forget token + saved web session, then log in headless and time it
//                    --reuse         force a login attempt via the saved session only (timing)
if (args.Contains("--seed-demo"))   // fake data for screenshots/demos: Smoke --seed-demo <folder>; run the app with UNIDASHBOARD_DATA_DIR=<folder>
{
    DemoSeed.Run(args[Array.IndexOf(args, "--seed-demo") + 1]);
    return;
}

if (args.Contains("--trace-login"))   // diagnostic: follow the real sign-in redirect chain WITHOUT credentials, print where it leads. Touches no data.
{
    // Smoke --trace-login moodle|uspace   → hosts, paths, status codes and form FIELD NAMES only (no values, no cookies, no query strings)
    var which = args[Array.IndexOf(args, "--trace-login") + 1];
    var (startUrl, done, hints) = which == "uspace"
        ? (new Uri("https://uspace.univie.ac.at/web/studium/anmeldeuebersicht"), new System.Text.RegularExpressions.Regex(@"^https://uspace\.univie\.ac\.at/(?!c/portal/login)"), new[] { "broker/shibboleth/login" })
        : (new Uri("https://moodle.univie.ac.at/login/index.php"), new System.Text.RegularExpressions.Regex(@"^https://moodle\.univie\.ac\.at/(?!login/)"), (string[]?)null);
    using var web = new UniDashboard.Core.Web.WebSession(["univie.ac.at"]) { Trace = s => Console.WriteLine("  " + s) };
    try
    {
        await new UniDashboard.Core.Web.UnivieLogin(web).RunAsync(startUrl, new(u => done.IsMatch(u.AbsoluteUri), null, hints));
        Console.WriteLine("reached the target without logging in");
    }
    catch (LoginRequiredException e) { Console.WriteLine("stopped (expected without credentials): " + e.Message); }
    catch (UniDashboard.Core.Web.WebSecurityException e) { Console.WriteLine("BLOCKED by the safety rules: " + e.Message); }
    return;
}

var db = new Database();
var settings = new AppSettings(db);
var tokens = new TokenStore();
var auth = new AuthService(settings, tokens);

if (args.Contains("--uspace"))
{
    var us = new UniDashboard.Core.Uspace.UspaceClient(settings);
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
    using var h = UniDashboard.Core.Ufind.UfindClient.CreateHttpClient();
    var svc = new UniDashboard.Core.Univie.UnivieSyncService(db, settings, new UniDashboard.Core.Uspace.UspaceClient(settings),
        new UniDashboard.Core.Ufind.UfindClient(h), new ConsoleNotifier(), headless: true, Console.WriteLine,
        new UniDashboard.Core.Ufind.CurriculumClient(h));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = await svc.RunAsync();
    Console.WriteLine($"univie sync: {r.Enrolments} enrolments, {r.Changed} changed, {r.Notified} toasts, error={r.Error ?? "none"} in {sw.Elapsed.TotalSeconds:F1}s");
    return;
}

if (args.Contains("--export-ics"))   // dev check: writes the .ics of the real data to the given path (does not touch export state)
{
    var path = args[Array.IndexOf(args, "--export-ics") + 1];
    var now = DateTimeOffset.Now;
    var items = UniDashboard.Core.Export.ExportBuilder.Build(db, new UniDashboard.Core.Export.ExportOptions(), now);
    var (seqs, _) = UniDashboard.Core.Export.IcsWriter.NextSequences(items, new Dictionary<string, (string, int)>());
    File.WriteAllText(path, UniDashboard.Core.Export.IcsWriter.Write(items, i => seqs[i.Key], now, 30), new System.Text.UTF8Encoding(false));
    Console.WriteLine($"{items.Count} items ({items.Count(i => i.Kind == UniDashboard.Core.Export.ExportKind.Exam)} exams) -> {path}");
    return;
}

if (args.Contains("--google-preview"))   // READ-ONLY: previews what a Google sync would do (never writes)
{
    using var gh = new HttpClient();
    var creds = new UniDashboard.Core.Google.GoogleCredentialStore();
    var gauth = new UniDashboard.Core.Google.GoogleAuth(gh, creds);
    var api = new UniDashboard.Core.Google.GoogleCalendarClient(gh, (f, ct) => gauth.GetAccessTokenAsync(f, ct));
    var svc = new UniDashboard.Core.Google.GoogleSyncService(db, settings, api);
    Console.WriteLine($"connected={gauth.IsConnected} target='{settings.GoogleTargetCalendarName}' scan=[{string.Join(",", settings.GoogleScanCalendarIds)}]");
    try
    {
        var pv = await svc.PreviewAsync();
        var pl = pv.Plan;
        Console.WriteLine($"create={pl.Count(UniDashboard.Core.Export.SyncActionKind.Create)} update={pl.Count(UniDashboard.Core.Export.SyncActionKind.Update)} delete={pl.Count(UniDashboard.Core.Export.SyncActionKind.Delete)} unchanged={pl.Count(UniDashboard.Core.Export.SyncActionKind.Unchanged)} dup={pl.Count(UniDashboard.Core.Export.SyncActionKind.SkipForeignDuplicate)} foreign={pv.Foreign.Count} scanned={pv.ScannedEvents} blocked={pl.DeletesBlocked}");
    }
    catch (Exception e) { Console.WriteLine("EXCEPTION: " + e); }
    return;
}

if (args.Contains("--register-agent"))   // (re)create the scheduled task, replacing the pre-rename one
{
    var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniDashboard", "bin", "UniDashboard.Agent.exe");
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

class ConsoleNotifier : UniDashboard.Core.Sync.INotifier
{
    public void Send(UniDashboard.Core.PendingNotification n) => Console.WriteLine($"[toast] {n.Title}: {n.Body}");
}
