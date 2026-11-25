namespace UniDashboard.Core.Export;

/// <summary>What we remember about an event we created in Google Calendar.</summary>
public record SyncStateRow(string ItemKey, string CalendarId, string EventId, string ContentHash, string? Etag, string? RemoteUpdated);

public enum SyncActionKind
{
    Create, Update, Delete,
    /// <summary>The event is already gone in Google: just drop our state.</summary>
    Forget,
    Unchanged,
    /// <summary>The user already has this event (old u:find/u:space import): we don't add a copy.</summary>
    SkipForeignDuplicate,
    /// <summary>The user changed our event in Google: we leave it alone (unless "overwrite my edits").</summary>
    SkipUserEdited,
    /// <summary>The user deleted our event in Google: we don't recreate it.</summary>
    SkipUserDeleted,
    /// <summary>Our event whose source disappeared, but the user edited it: kept.</summary>
    KeepOrphanEdited,
}

public record SyncAction(SyncActionKind Kind, string ItemKey, ExportItem? Item = null, SyncStateRow? State = null, RemoteEvent? Remote = null,
    ForeignMatch? Match = null, string? Note = null, string? ReplacesKey = null);

public class SyncOptions
{
    public string CalendarId { get; init; } = "";
    public bool OverwriteUserEdits { get; init; }
    /// <summary>False when the data source failed or is empty: nothing is deleted then.</summary>
    public bool SourceHealthy { get; init; } = true;
    /// <summary>Events that ended before this moment are history: orphaned ones are kept, never deleted. Null = no protection.</summary>
    public DateTimeOffset? KeepPastBefore { get; init; }
    public int MassDeleteMin { get; init; } = 10;
    public double MassDeleteFraction { get; init; } = 0.25;
}

public class SyncPlan
{
    public List<SyncAction> Actions { get; } = [];
    public bool DeletesBlocked { get; set; }
    public string? GuardReason { get; set; }
    public List<string> Notes { get; } = [];

    public IEnumerable<SyncAction> Of(SyncActionKind k) => Actions.Where(a => a.Kind == k);
    public int Count(SyncActionKind k) => Actions.Count(a => a.Kind == k);
    public bool HasChanges => Actions.Any(a => a.Kind is SyncActionKind.Create or SyncActionKind.Update or SyncActionKind.Delete or SyncActionKind.Forget);
}

/// <summary>
/// Decides, without any I/O, what a sync would do. Safety properties (all unit-tested):
/// only events carrying our marker / present in our state are ever updated or deleted; edits made in Google are never
/// overwritten by default; events the user already has (old imports) are not duplicated; moved sessions keep their Google
/// event; nothing is deleted when the source is unhealthy, and a mass-delete guard stops runaway deletions.
/// </summary>
public static class SyncPlanner
{
    public static SyncPlan Plan(
        IReadOnlyList<ExportItem> desired,
        IReadOnlyDictionary<string, SyncStateRow> allStates,
        IReadOnlyList<RemoteEvent> remoteManaged,
        IReadOnlyList<ForeignMatch> foreignMatches,
        SyncOptions options)
    {
        var plan = new SyncPlan();

        // Only state for the current target calendar counts; events in a previously used calendar are left untouched.
        var states = allStates.Values.Where(s => s.CalendarId == options.CalendarId).ToDictionary(s => s.ItemKey);
        var desiredByKey = desired.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Last());
        var remoteById = remoteManaged.Where(r => !r.Cancelled).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var remoteByKey = remoteManaged.Where(r => !r.Cancelled && r.ManagedKey is not null).GroupBy(r => r.ManagedKey!).ToDictionary(g => g.Key, g => g.First());
        var foreignByKey = foreignMatches.GroupBy(m => m.ItemKey).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Confidence).First());

        var consumedStates = new HashSet<string>();
        var consumedRemote = new HashSet<string>();

        foreach (var item in desiredByKey.Values.OrderBy(i => i.Start).ThenBy(i => i.Key, StringComparer.Ordinal))
        {
            states.TryGetValue(item.Key, out var st);
            string? replaces = null;

            // A moved session continues the Google event of the slot it moved from.
            if (st is null && item.PrevKey is not null && !desiredByKey.ContainsKey(item.PrevKey) && states.TryGetValue(item.PrevKey, out var prev))
            { st = prev; replaces = item.PrevKey; }

            // State lost but our marker is on an event in Google: adopt it (never duplicate).
            if (st is null && remoteByKey.TryGetValue(item.Key, out var adopted))
                st = new SyncStateRow(item.Key, options.CalendarId, adopted.Id, adopted.ManagedHash ?? "", adopted.Etag, adopted.Updated);

            if (st is not null)
            {
                consumedStates.Add(st.ItemKey);
                if (!remoteById.TryGetValue(st.EventId, out var remote))
                {
                    plan.Actions.Add(new(SyncActionKind.SkipUserDeleted, item.Key, item, st, null, null, "deleted in Google"));
                    continue;
                }
                consumedRemote.Add(remote.Id);

                if (st.ContentHash == item.Hash && replaces is null) { plan.Actions.Add(new(SyncActionKind.Unchanged, item.Key, item, st, remote)); continue; }

                var edited = st.Etag is not null && remote.Etag != st.Etag;
                plan.Actions.Add(edited && !options.OverwriteUserEdits
                    ? new(SyncActionKind.SkipUserEdited, item.Key, item, st, remote, null, "changed in Google since the last sync")
                    : new(SyncActionKind.Update, item.Key, item, st, remote, null, replaces is null ? null : "moved", replaces));
                continue;
            }

            plan.Actions.Add(foreignByKey.TryGetValue(item.Key, out var fm)
                ? new(SyncActionKind.SkipForeignDuplicate, item.Key, item, null, fm.Remote, fm, $"already in “{fm.Remote.CalendarName}”")
                : new(SyncActionKind.Create, item.Key, item));
        }

        // ---- orphans: our events whose source is gone
        if (!options.SourceHealthy)
            plan.Notes.Add("The data source looks unhealthy or empty — nothing is deleted this time.");
        else
        {
            foreach (var st in states.Values.Where(s => !consumedStates.Contains(s.ItemKey)))
            {
                if (!remoteById.TryGetValue(st.EventId, out var remote)) { plan.Actions.Add(new(SyncActionKind.Forget, st.ItemKey, null, st)); continue; }
                consumedRemote.Add(remote.Id);
                if (IsHistory(remote, options)) continue;   // already happened: stays in the calendar as history
                var edited = st.Etag is not null && remote.Etag != st.Etag;
                plan.Actions.Add(edited && !options.OverwriteUserEdits
                    ? new(SyncActionKind.KeepOrphanEdited, st.ItemKey, null, st, remote, null, "no longer exported, but you changed it in Google")
                    : new(SyncActionKind.Delete, st.ItemKey, null, st, remote, null, "no longer exists in your timetable"));
            }

            // Events with our marker but unknown to the state (state was lost) and not wanted any more.
            foreach (var r in remoteById.Values.Where(r => r.Managed && !consumedRemote.Contains(r.Id) && (r.ManagedKey is null || !desiredByKey.ContainsKey(r.ManagedKey)) && !IsHistory(r, options)))
                plan.Actions.Add(new(SyncActionKind.Delete, r.ManagedKey ?? r.Id, null, null, r, null, "no longer exists in your timetable (not in sync state)"));

            var deletes = plan.Count(SyncActionKind.Delete);
            var limit = Math.Max(options.MassDeleteMin, (int)Math.Ceiling(Math.Max(1, remoteById.Count) * options.MassDeleteFraction));
            if (deletes > limit)
            {
                plan.DeletesBlocked = true;
                plan.GuardReason = $"{deletes} deletions planned (more than {limit}); not deleting anything unless you confirm.";
            }
        }
        return plan;
    }

    static bool IsHistory(RemoteEvent r, SyncOptions o) => o.KeepPastBefore is { } t && (r.End ?? r.Start) is { } end && end < t;
}
