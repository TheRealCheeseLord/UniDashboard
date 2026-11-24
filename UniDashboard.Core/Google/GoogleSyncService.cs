using System.Globalization;
using UniDashboard.Core.Export;

namespace UniDashboard.Core.Google;

public record GooglePreview(SyncPlan Plan, List<ForeignMatch> Foreign, List<GoogleCalendarInfo> Calendars, List<string> Warnings, int ScannedEvents)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public record GoogleApplyResult(int Created, int Updated, int Deleted, int Forgotten, int Skipped, List<string> Errors, bool DeletesBlocked)
{
    public string Summary =>
        $"{Created} created, {Updated} updated, {Deleted} deleted" + (Forgotten > 0 ? $", {Forgotten} forgotten" : "") +
        (Skipped > 0 ? $", {Skipped} skipped" : "") + (Errors.Count > 0 ? $", {Errors.Count} problem(s)" : "") +
        (DeletesBlocked ? " — deletions held back (confirmation needed)" : "");
}

public record ForeignDeleteResult(int Deleted, int AlreadyGone, List<string> Skipped);

/// <summary>
/// Syncs lectures and registered exams to one Google calendar chosen by the user, with the safety rules of <see cref="SyncPlanner"/>.
/// Preview never writes; Apply persists the state after every single event (a crash leaves a consistent map).
/// </summary>
public class GoogleSyncService(Database db, AppSettings settings, IGoogleCalendar api, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
{
    readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);

    ExportOptions ExportOpts(bool everything = false) => new()
    {
        Sessions = settings.ExportSessions, RegisteredExams = settings.ExportExams, FromTodayOnly = !everything && settings.ExportFutureOnly,
    };

    // ---------- preview

    /// <param name="sourceHealthy">False when the u:space sync failed this run: nothing will be deleted.</param>
    public async Task<GooglePreview> PreviewAsync(bool sourceHealthy = true, CancellationToken ct = default, bool removeEverything = false)
    {
        var cal = settings.GoogleTargetCalendarId;
        if (string.IsNullOrEmpty(cal)) throw new InvalidOperationException("Choose the Google calendar to sync into first.");

        var calendars = await api.ListCalendarsAsync(ct);
        var target = calendars.FirstOrDefault(c => c.Id == cal) ?? throw new InvalidOperationException("The chosen Google calendar no longer exists.");
        if (!target.CanWrite) throw new InvalidOperationException($"“{target.Name}” is read-only for you; choose a calendar you can edit.");

        var now = _now();
        var warnings = new List<string>();
        var desired = removeEverything ? [] : ExportBuilder.Build(db, ExportOpts(), now);
        var states = db.GetGoogleState();

        var managed = await api.ListManagedEventsAsync(cal, target.Name, ct);

        // Look for the user's own earlier imports in the calendars they ticked (+ the target).
        var scan = settings.GoogleScanCalendarIds.Append(cal).Distinct().ToList();
        var foreign = new List<ForeignMatch>();
        var scanned = 0;
        if (desired.Count > 0)
        {
            var from = desired.Min(d => d.Start).AddDays(-1);
            var to = desired.Max(d => d.End).AddDays(1);
            var remote = new List<RemoteEvent>();
            foreach (var id in scan)
            {
                var info = calendars.FirstOrDefault(c => c.Id == id);
                if (info is null) { warnings.Add("A calendar chosen for scanning no longer exists."); continue; }
                remote.AddRange(await api.ListEventsAsync(id, info.Name, from, to, ct));
            }
            scanned = remote.Count;
            foreign = ImportedEventMatcher.Match(remote, desired);
        }

        var plan = SyncPlanner.Plan(desired, states, managed, foreign, new SyncOptions
        {
            CalendarId = cal, OverwriteUserEdits = settings.GoogleOverwriteEdits,
            SourceHealthy = sourceHealthy && (desired.Count > 0 || removeEverything),
        });
        if (removeEverything) plan.Notes.Add("Removing every event this app created in this calendar.");
        else if (desired.Count == 0 && states.Count > 0) plan.Notes.Add("Nothing selected for export — existing synced events are kept.");
        return new(plan, foreign, calendars, warnings, scanned);
    }

    // ---------- apply

    public async Task<GoogleApplyResult> ApplyAsync(GooglePreview preview, bool allowMassDelete = false, CancellationToken ct = default)
    {
        await using var gate = await SyncLock.AcquireAsync("google-sync", TimeSpan.FromSeconds(60), ct);   // never together with the agent / another window
        var cal = settings.GoogleTargetCalendarId;
        var calName = preview.Calendars.FirstOrDefault(c => c.Id == cal)?.Name ?? cal;
        int created = 0, updated = 0, deleted = 0, forgotten = 0, skipped = 0;
        var errors = new List<string>();
        var holdDeletes = preview.Plan.DeletesBlocked && !allowMassDelete;

        foreach (var a in preview.Plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (a.Kind)
                {
                    case SyncActionKind.Create:
                    {
                        var data = GoogleEventData.From(a.Item!);
                        try
                        {
                            var r = await api.InsertAsync(cal, calName, data, ct);
                            db.SaveGoogleState(new(a.ItemKey, cal, r.Id, a.Item!.Hash, r.Etag, r.Updated));
                            created++;
                        }
                        catch (GoogleConflictException)
                        {
                            // Deterministic id already exists: adopt it only if it is really ours.
                            var existing = await api.GetAsync(cal, calName, data.Id, ct);
                            if (existing is { Managed: true } && existing.ManagedKey == a.ItemKey)
                            {
                                db.SaveGoogleState(new(a.ItemKey, cal, existing.Id, existing.ManagedHash ?? "", existing.Etag, existing.Updated));
                                skipped++;
                            }
                            else { errors.Add($"“{a.Item!.Title}”: the id is taken by a different event — skipped."); skipped++; }
                        }
                        break;
                    }
                    case SyncActionKind.Update:
                    {
                        var data = GoogleEventData.From(a.Item!) with { Id = a.State!.EventId };
                        try
                        {
                            var r = await api.PatchAsync(cal, calName, a.State.EventId, data, a.Remote?.Etag, ct);
                            if (a.ReplacesKey is not null) db.DeleteGoogleState(a.ReplacesKey);
                            db.SaveGoogleState(new(a.ItemKey, cal, r.Id, a.Item!.Hash, r.Etag, r.Updated));
                            updated++;
                        }
                        catch (GooglePreconditionException) { errors.Add($"“{a.Item!.Title}”: changed in Google while syncing — skipped."); skipped++; }
                        catch (GoogleNotFoundException) { errors.Add($"“{a.Item!.Title}”: no longer exists in Google — skipped."); skipped++; }
                        break;
                    }
                    case SyncActionKind.Delete:
                    {
                        if (holdDeletes) { skipped++; break; }
                        // Defence in depth: never delete anything that is not marked as ours.
                        if (a.Remote is null || !a.Remote.Managed) { skipped++; break; }
                        try { await api.DeleteAsync(cal, a.Remote.Id, a.Remote.Etag, ct); deleted++; }
                        catch (GoogleNotFoundException) { forgotten++; }
                        catch (GooglePreconditionException) { errors.Add($"“{a.Remote.Summary}”: changed in Google — not deleted."); skipped++; break; }
                        db.DeleteGoogleState(a.ItemKey);
                        break;
                    }
                    case SyncActionKind.Forget:
                        db.DeleteGoogleState(a.ItemKey);
                        forgotten++;
                        break;
                    default:
                        if (a.Kind != SyncActionKind.Unchanged) skipped++;
                        break;
                }
            }
            catch (GoogleApiException e) { errors.Add($"{a.Item?.Title ?? a.ItemKey}: {e.Message}"); skipped++; }
        }

        var result = new GoogleApplyResult(created, updated, deleted, forgotten, skipped, errors, holdDeletes);
        db.AddGoogleLog(_now(), "apply", result.Summary);
        if (errors.Count == 0 || created + updated + deleted > 0) settings.GoogleApplied = true;
        log?.Invoke("google: " + result.Summary);
        return result;
    }

    /// <summary>Preview + apply in one go (used by the background agent). Deletions beyond the guard are held back, clean-up never runs.</summary>
    public async Task<GoogleApplyResult> SyncAsync(bool sourceHealthy, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(sourceHealthy, ct);
        return await ApplyAsync(preview, allowMassDelete: false, ct);
    }

    /// <summary>Removes every event this app created in the target calendar (explicit user action).</summary>
    public async Task<GoogleApplyResult> RemoveAllManagedAsync(CancellationToken ct = default)
    {
        var preview = await PreviewAsync(sourceHealthy: true, ct, removeEverything: true);
        return await ApplyAsync(preview, allowMassDelete: true, ct);
    }

    // ---------- clean-up of the user's own old imports (explicit, confirmed, never automatic)

    public async Task<ForeignDeleteResult> DeleteForeignAsync(IEnumerable<ForeignMatch> selected, IReadOnlyList<GoogleCalendarInfo> calendars, CancellationToken ct = default)
    {
        await using var gate = await SyncLock.AcquireAsync("google-sync", TimeSpan.FromSeconds(60), ct);
        var all = ExportBuilder.Build(db, ExportOpts(everything: true), _now());
        int deleted = 0, gone = 0;
        var skipped = new List<string>();

        foreach (var m in selected)
        {
            ct.ThrowIfCancellationRequested();
            var r = m.Remote;
            var label = $"“{r.Summary}” ({r.Start?.ToLocalTime().ToString("ddd d.M. HH:mm", CultureInfo.GetCultureInfo("de-AT"))}, {r.CalendarName})";
            if (r.Managed) { skipped.Add(label + ": created by this app"); continue; }

            var cal = calendars.FirstOrDefault(c => c.Id == r.CalendarId);
            if (cal is null || !cal.CanWrite) { skipped.Add(label + ": calendar is read-only"); continue; }

            try
            {
                // Re-read and re-verify right before deleting: it must still be unchanged and still look like an import.
                var current = await api.GetAsync(r.CalendarId, r.CalendarName, r.Id, ct);
                if (current is null || current.Cancelled) { gone++; continue; }
                if (current.Managed) { skipped.Add(label + ": created by this app"); continue; }
                if (current.Etag != r.Etag) { skipped.Add(label + ": changed since the scan"); continue; }
                if (ImportedEventMatcher.Match([current], all).Count == 0) { skipped.Add(label + ": no longer looks like an import"); continue; }

                await api.DeleteAsync(r.CalendarId, r.Id, current.Etag, ct);
                deleted++;
            }
            catch (GoogleNotFoundException) { gone++; }
            catch (GooglePreconditionException) { skipped.Add(label + ": changed while deleting"); }
            catch (GoogleApiException e) { skipped.Add(label + ": " + e.Message); }
        }

        db.AddGoogleLog(_now(), "cleanup", $"{deleted} old imported event(s) deleted, {gone} already gone, {skipped.Count} skipped");
        log?.Invoke($"google cleanup: {deleted} deleted, {skipped.Count} skipped");
        return new(deleted, gone, skipped);
    }
}
