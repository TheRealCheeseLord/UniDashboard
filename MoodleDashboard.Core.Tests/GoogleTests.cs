using System.Net;
using System.Text;
using System.Text.Json;
using MoodleDashboard.Core;
using MoodleDashboard.Core.Export;
using MoodleDashboard.Core.Google;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.Core.Tests;

// ============================================================ in-memory Google Calendar (mimics etags, 409, 412, 404)

sealed class FakeGoogle : IGoogleCalendar
{
    public List<GoogleCalendarInfo> Calendars { get; } = [new("main", "Main", "owner", true), new("uni", "Studium", "owner", false), new("ro", "Holidays", "reader", false)];
    public Dictionary<string, Dictionary<string, RemoteEvent>> Events { get; } = new() { ["main"] = [], ["uni"] = [], ["ro"] = [] };
    public int Writes { get; private set; }
    int _etag = 1;
    string NextEtag() => $"\"e{_etag++}\"";

    string Name(string id) => Calendars.First(c => c.Id == id).Name;

    public Task<List<GoogleCalendarInfo>> ListCalendarsAsync(CancellationToken ct = default) => Task.FromResult(Calendars.ToList());

    public Task<List<RemoteEvent>> ListEventsAsync(string calendarId, string calendarName, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
        Task.FromResult(Events[calendarId].Values.Where(e => !e.Cancelled && e.Start is { } s && s < timeMax && (e.End ?? s) > timeMin).ToList());

    public Task<List<RemoteEvent>> ListManagedEventsAsync(string calendarId, string calendarName, CancellationToken ct = default) =>
        Task.FromResult(Events[calendarId].Values.Where(e => e.Managed && !e.Cancelled).ToList());

    public Task<RemoteEvent?> GetAsync(string calendarId, string calendarName, string eventId, CancellationToken ct = default) =>
        Task.FromResult(Events[calendarId].TryGetValue(eventId, out var e) ? e : null);

    public Task<RemoteEvent> InsertAsync(string calendarId, string calendarName, GoogleEventData d, CancellationToken ct = default)
    {
        if (Events[calendarId].ContainsKey(d.Id)) throw new GoogleConflictException("exists");
        Writes++;
        var e = new RemoteEvent(calendarId, Name(calendarId), d.Id, d.Id + "@google.com", d.Summary, d.Start, d.End, d.AllDay, d.Location, d.Description, NextEtag(),
            "2026-01-01T00:00:00Z", false, true, d.ItemKey, d.ItemHash);
        Events[calendarId][d.Id] = e;
        return Task.FromResult(e);
    }

    public Task<RemoteEvent> PatchAsync(string calendarId, string calendarName, string eventId, GoogleEventData d, string? ifMatchEtag, CancellationToken ct = default)
    {
        if (!Events[calendarId].TryGetValue(eventId, out var cur)) throw new GoogleNotFoundException("gone");
        if (ifMatchEtag is not null && ifMatchEtag != cur.Etag) throw new GooglePreconditionException("etag");
        Writes++;
        var e = cur with { Summary = d.Summary, Start = d.Start, End = d.End, AllDay = d.AllDay, Location = d.Location, Description = d.Description,
            Etag = NextEtag(), Managed = true, ManagedKey = d.ItemKey, ManagedHash = d.ItemHash };
        Events[calendarId][eventId] = e;
        return Task.FromResult(e);
    }

    public Task DeleteAsync(string calendarId, string eventId, string? ifMatchEtag, CancellationToken ct = default)
    {
        if (!Events[calendarId].TryGetValue(eventId, out var cur)) throw new GoogleNotFoundException("gone");
        if (ifMatchEtag is not null && ifMatchEtag != cur.Etag) throw new GooglePreconditionException("etag");
        Writes++;
        Events[calendarId].Remove(eventId);
        return Task.CompletedTask;
    }

    // ---- helpers to simulate what the user does in Google
    public void UserEdit(string calendarId, string id) => Events[calendarId][id] = Events[calendarId][id] with { Summary = Events[calendarId][id].Summary + " (my note)", Etag = NextEtag() };
    public void UserDelete(string calendarId, string id) => Events[calendarId].Remove(id);
    public RemoteEvent AddForeign(string calendarId, string summary, DateTimeOffset start, DateTimeOffset end, string? location = null, string? desc = null)
    {
        var e = new RemoteEvent(calendarId, Name(calendarId), "f" + Guid.NewGuid().ToString("N")[..10], Guid.NewGuid() + "@x", summary, start, end, false, location, desc, NextEtag(), null);
        Events[calendarId][e.Id] = e;
        return e;
    }
}

// ============================================================ SyncPlanner

public class SyncPlannerTests
{
    static readonly DateTimeOffset T0 = new(2099, 2, 3, 9, 0, 0, TimeSpan.FromHours(1));
    const string Cal = "main";

    static ExportItem Item(string key, int dayOffset = 0, string title = "Algebra (VO)", string? prevKey = null) =>
        new(key, ExportKind.Session, title, T0.AddDays(dayOffset), T0.AddDays(dayOffset).AddHours(1), false, "HS 1", "d", "https://u", prevKey, "010001", "1", "2099W", "Algebra");

    static RemoteEvent Managed(string key, string id, string etag, string hash = "h") =>
        new(Cal, "Main", id, null, "x", T0, T0.AddHours(1), false, null, null, etag, null, false, true, key, hash);

    static SyncStateRow State(ExportItem i, string id, string etag, string? hash = null) => new(i.Key, Cal, id, hash ?? i.Hash, etag, null);

    static SyncOptions Opt(bool overwrite = false, bool healthy = true) => new() { CalendarId = Cal, OverwriteUserEdits = overwrite, SourceHealthy = healthy };

    static SyncPlan Plan(IReadOnlyList<ExportItem> desired, IEnumerable<SyncStateRow>? states = null, IEnumerable<RemoteEvent>? remote = null,
        IEnumerable<ForeignMatch>? foreign = null, SyncOptions? opt = null) =>
        SyncPlanner.Plan(desired, (states ?? []).ToDictionary(s => s.ItemKey), (remote ?? []).ToList(), (foreign ?? []).ToList(), opt ?? Opt());

    [Fact]
    public void New_items_are_created()
    {
        var p = Plan([Item("a"), Item("b", 1)]);
        Assert.Equal(2, p.Count(SyncActionKind.Create));
        Assert.False(p.DeletesBlocked);
    }

    [Fact]
    public void Unchanged_items_do_nothing()
    {
        var a = Item("a");
        var p = Plan([a], [State(a, "id1", "e1")], [Managed("a", "id1", "e1", a.Hash)]);
        Assert.Equal(1, p.Count(SyncActionKind.Unchanged));
        Assert.False(p.HasChanges);
    }

    [Fact]
    public void Changed_content_is_updated()
    {
        var a = Item("a");
        var p = Plan([a with { Location = "HS 2" }], [State(a, "id1", "e1")], [Managed("a", "id1", "e1")]);
        Assert.Equal(1, p.Count(SyncActionKind.Update));
    }

    [Fact]
    public void An_event_edited_in_google_is_not_overwritten_unless_requested()
    {
        var a = Item("a");
        var changed = a with { Location = "HS 2" };
        var remote = Managed("a", "id1", "e-USER");
        Assert.Equal(1, Plan([changed], [State(a, "id1", "e1")], [remote]).Count(SyncActionKind.SkipUserEdited));
        Assert.Equal(1, Plan([changed], [State(a, "id1", "e1")], [remote], opt: Opt(overwrite: true)).Count(SyncActionKind.Update));
    }

    [Fact]
    public void An_event_deleted_in_google_is_not_recreated()
    {
        var a = Item("a");
        var p = Plan([a], [State(a, "id1", "e1")], []);
        Assert.Equal(1, p.Count(SyncActionKind.SkipUserDeleted));
        Assert.Equal(0, p.Count(SyncActionKind.Create));
    }

    [Fact]
    public void Old_imports_are_skipped_not_duplicated()
    {
        var a = Item("a");
        var foreign = new ForeignMatch(new RemoteEvent("uni", "Studium", "f1", null, "010001-1 VO Algebra", T0, T0.AddHours(1), false, null, null, "e", null), "a", MatchConfidence.Strong, "x");
        var p = Plan([a], foreign: [foreign]);
        Assert.Equal(1, p.Count(SyncActionKind.SkipForeignDuplicate));
        Assert.Equal(0, p.Count(SyncActionKind.Create));
    }

    [Fact]
    public void Moved_session_updates_the_existing_event_in_place_and_rekeys()
    {
        var old = Item("old");
        var moved = Item("new", 7, prevKey: "old");
        var p = Plan([moved], [State(old, "id1", "e1")], [Managed("old", "id1", "e1")]);
        var u = Assert.Single(p.Actions, a => a.Kind == SyncActionKind.Update);
        Assert.Equal("old", u.ReplacesKey);
        Assert.Equal("id1", u.State!.EventId);
        Assert.Equal(0, p.Count(SyncActionKind.Create));
        Assert.Equal(0, p.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void Disappeared_sources_are_deleted_only_when_we_own_the_event()
    {
        var a = Item("a");
        var p = Plan([], [State(a, "id1", "e1")], [Managed("a", "id1", "e1")]);
        Assert.Equal(1, p.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void Orphan_that_the_user_edited_is_kept()
    {
        var a = Item("a");
        var p = Plan([], [State(a, "id1", "e1")], [Managed("a", "id1", "e-USER")]);
        Assert.Equal(1, p.Count(SyncActionKind.KeepOrphanEdited));
        Assert.Equal(0, p.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void Orphan_already_gone_in_google_is_just_forgotten()
    {
        var a = Item("a");
        var p = Plan([], [State(a, "id1", "e1")], []);
        Assert.Equal(1, p.Count(SyncActionKind.Forget));
    }

    [Fact]
    public void Unhealthy_source_never_deletes()
    {
        var a = Item("a");
        var p = Plan([], [State(a, "id1", "e1")], [Managed("a", "id1", "e1")], opt: Opt(healthy: false));
        Assert.Equal(0, p.Count(SyncActionKind.Delete));
        Assert.Equal(0, p.Count(SyncActionKind.Forget));
        Assert.NotEmpty(p.Notes);
    }

    [Fact]
    public void Mass_delete_guard_holds_back_runaway_deletions()
    {
        var items = Enumerable.Range(0, 30).Select(i => Item($"k{i}", i)).ToList();
        var states = items.Select((i, n) => State(i, $"id{n}", "e1")).ToList();
        var remote = items.Select((i, n) => Managed(i.Key, $"id{n}", "e1")).ToList();
        var p = Plan([], states, remote);                       // everything disappeared at once
        Assert.True(p.DeletesBlocked);
        Assert.NotNull(p.GuardReason);

        // A few deletions are fine.
        var few = Plan(items.Skip(5).ToList(), states, remote);
        Assert.False(few.DeletesBlocked);
        Assert.Equal(5, few.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void State_of_another_calendar_is_ignored_and_left_alone()
    {
        var a = Item("a");
        var other = new SyncStateRow("a", "other-cal", "idX", a.Hash, "e1", null);
        var p = Plan([a], [other], []);
        Assert.Equal(1, p.Count(SyncActionKind.Create));
        Assert.Equal(0, p.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void Lost_state_adopts_the_marked_event_instead_of_duplicating()
    {
        var a = Item("a");
        var p = Plan([a], [], [Managed("a", "id1", "e1", a.Hash)]);
        Assert.Equal(1, p.Count(SyncActionKind.Unchanged));
        Assert.Equal(0, p.Count(SyncActionKind.Create));
    }

    [Fact]
    public void Marked_event_without_state_that_is_no_longer_wanted_is_deleted()
    {
        var p = Plan([], [], [Managed("ghost", "idG", "e1")]);
        Assert.Equal(1, p.Count(SyncActionKind.Delete));
    }

    [Fact]
    public void Unmarked_events_are_never_planned_for_deletion()
    {
        var unmarked = new RemoteEvent(Cal, "Main", "u1", null, "Dentist", T0, T0.AddHours(1), false, null, null, "e", null);
        var p = Plan([], [], [unmarked]);
        Assert.Equal(0, p.Count(SyncActionKind.Delete));
    }
}

// ============================================================ GoogleSyncService against the in-memory calendar

public class GoogleSyncServiceTests
{
    const string Key = "010004-1-2099W";
    static readonly DateTimeOffset Now = new(new DateTime(2099, 1, 1, 8, 0, 0, DateTimeKind.Local));
    static DateTimeOffset At(int month, int day, int h = 9) => new(new DateTime(2099, month, day, h, 0, 0, DateTimeKind.Local));

    sealed class Env
    {
        public Database Db = new(Path.GetTempFileName());
        public AppSettings Settings;
        public FakeGoogle G = new();
        public GoogleSyncService Svc;
        public EnrolmentRecord Enr = new() { Key = Key, LvNr = "010004", GroupNr = "1", Semester = "2099W", Title = "Einführung", Type = "PS", Status = "ANGEMELDET", LastSeenAt = Now, Active = true };
        public List<SessionRecord> Sessions = [];

        public Env(string target = "main")
        {
            Settings = new AppSettings(Db) { GoogleTargetCalendarId = target };
            Svc = new GoogleSyncService(Db, Settings, G, () => Now);
        }

        public SessionRecord AddSession(string key, DateTimeOffset begin, string room = "R1", SessionChange ch = SessionChange.None, string? prevKey = null, SessionStatus st = SessionStatus.Active)
        {
            var s = new SessionRecord { Key = key, EnrolmentKey = Key, Begin = begin, End = begin.AddMinutes(90), Room = room, Hash = key + room, Change = ch, PrevKey = prevKey, Status = st, FirstSeenAt = Now, LastChangedAt = Now };
            Sessions.RemoveAll(x => x.Key == key);
            Sessions.Add(s);
            return s;
        }

        public void Save() => Db.SaveEnrolment(Enr, Sessions, null, null);
        public Task<GoogleApplyResult> Sync(bool healthy = true) => Svc.SyncAsync(healthy);
    }

    [Fact]
    public async Task First_sync_creates_marked_events_and_a_second_run_changes_nothing()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.Save();

        var r1 = await e.Sync();
        Assert.Equal(2, r1.Created);
        Assert.All(e.G.Events["main"].Values, ev => { Assert.True(ev.Managed); Assert.NotNull(ev.ManagedKey); });
        var writes = e.G.Writes;

        var r2 = await e.Sync();
        Assert.Equal(0, r2.Created + r2.Updated + r2.Deleted);
        Assert.Equal(writes, e.G.Writes);                       // idempotent: no further API writes
    }

    [Fact]
    public async Task Changed_session_is_patched_and_the_event_keeps_its_id()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.Save();
        await e.Sync();
        var id = e.G.Events["main"].Keys.Single();

        e.AddSession("s1", At(2, 3), room: "R2"); e.Save();
        var r = await e.Sync();
        Assert.Equal(1, r.Updated);
        Assert.Equal(id, e.G.Events["main"].Keys.Single());
        Assert.Equal("R2", e.G.Events["main"][id].Location);
    }

    [Fact]
    public async Task Moved_session_moves_the_same_google_event()
    {
        var e = new Env();
        e.AddSession("s-old", At(2, 3)); e.Save();
        await e.Sync();
        var id = e.G.Events["main"].Keys.Single();

        e.Sessions.Clear();
        e.AddSession("s-new", At(2, 12), ch: SessionChange.Moved, prevKey: "s-old"); e.Save();
        var r = await e.Sync();
        Assert.Equal(1, r.Updated);
        Assert.Equal(0, r.Created + r.Deleted);
        Assert.Equal(At(2, 12), e.G.Events["main"][id].Start);
        var state = e.Db.GetGoogleState();
        Assert.Contains("session:s-new", state.Keys);
        Assert.DoesNotContain("session:s-old", state.Keys);
    }

    [Fact]
    public async Task User_edits_in_google_are_never_overwritten()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.Save();
        await e.Sync();
        var id = e.G.Events["main"].Keys.Single();
        e.G.UserEdit("main", id);

        e.AddSession("s1", At(2, 3), room: "R2"); e.Save();
        var r = await e.Sync();
        Assert.Equal(0, r.Updated);
        Assert.Contains("(my note)", e.G.Events["main"][id].Summary);
        Assert.Equal("R1", e.G.Events["main"][id].Location);

        e.Settings.GoogleOverwriteEdits = true;
        Assert.Equal(1, (await e.Sync()).Updated);
    }

    [Fact]
    public async Task Event_deleted_by_the_user_is_not_recreated()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.Save();
        await e.Sync();
        e.G.UserDelete("main", e.G.Events["main"].Keys.Single());
        var r = await e.Sync();
        Assert.Equal(0, r.Created);
        Assert.Empty(e.G.Events["main"]);
    }

    [Fact]
    public async Task Cancelled_session_deletes_only_our_event_and_never_a_foreign_one()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.Save();
        var dentist = e.G.AddForeign("main", "Dentist", At(2, 3, 9), At(2, 3, 10));
        await e.Sync();

        e.AddSession("s1", At(2, 3), st: SessionStatus.Removed, ch: SessionChange.Removed); e.Save();
        var r = await e.Sync();
        Assert.Equal(1, r.Deleted);
        Assert.Contains(dentist.Id, e.G.Events["main"].Keys);
        Assert.Equal(2, e.G.Events["main"].Count);              // dentist + s2
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_source_is_unhealthy_or_empty()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.Save();
        await e.Sync();

        e.Sessions.Clear(); e.Save();
        var r = await e.Sync();                                  // desired is empty
        Assert.Equal(0, r.Deleted);
        Assert.Single(e.G.Events["main"]);

        e.AddSession("s1", At(2, 3), st: SessionStatus.Removed); e.Save();
        Assert.Equal(0, (await e.Sync(healthy: false)).Deleted);
        Assert.Single(e.G.Events["main"]);
    }

    [Fact]
    public async Task Mass_deletion_is_held_back_until_confirmed()
    {
        var e = new Env();
        for (var i = 0; i < 30; i++) e.AddSession($"s{i}", At(2, 1).AddDays(i));
        e.AddSession("keep", At(5, 1));
        e.Save();
        await e.Sync();
        Assert.Equal(31, e.G.Events["main"].Count);

        e.Sessions.RemoveAll(s => s.Key != "keep"); e.Save();
        var preview = await e.Svc.PreviewAsync();
        Assert.True(preview.Plan.DeletesBlocked);
        var r = await e.Svc.ApplyAsync(preview, allowMassDelete: false);
        Assert.True(r.DeletesBlocked);
        Assert.Equal(0, r.Deleted);
        Assert.Equal(31, e.G.Events["main"].Count);

        var confirmed = await e.Svc.ApplyAsync(preview, allowMassDelete: true);
        Assert.Equal(30, confirmed.Deleted);
        Assert.Single(e.G.Events["main"]);
    }

    [Fact]
    public async Task Old_u_find_imports_are_skipped_and_listed_but_untouched()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.Save();
        var old1 = e.G.AddForeign("uni", "010004-1 PS Einführung", At(2, 3), At(2, 3).AddMinutes(90), "R1");
        e.Settings.GoogleScanCalendarIds = ["uni"];

        var preview = await e.Svc.PreviewAsync();
        Assert.Single(preview.Foreign);
        var r = await e.Svc.ApplyAsync(preview);
        Assert.Equal(1, r.Created);                              // only s2
        Assert.Contains(old1.Id, e.G.Events["uni"].Keys);
        Assert.Equal(old1, e.G.Events["uni"][old1.Id]);
    }

    [Fact]
    public async Task Cleanup_deletes_only_the_selected_verified_events()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.AddSession("s3", At(2, 17)); e.Save();
        var o1 = e.G.AddForeign("uni", "010004-1 PS Einführung", At(2, 3), At(2, 3).AddMinutes(90));
        var o2 = e.G.AddForeign("uni", "010004-1 PS Einführung", At(2, 10), At(2, 10).AddMinutes(90));
        var o3 = e.G.AddForeign("uni", "010004-1 PS Einführung", At(2, 17), At(2, 17).AddMinutes(90));
        var dentist = e.G.AddForeign("uni", "Dentist", At(2, 3), At(2, 3).AddMinutes(90));
        e.Settings.GoogleScanCalendarIds = ["uni"];
        var preview = await e.Svc.PreviewAsync();
        Assert.Equal(3, preview.Foreign.Count);

        // o2 is edited by the user after the scan; o3 is not selected.
        e.G.UserEdit("uni", o2.Id);
        var result = await e.Svc.DeleteForeignAsync(preview.Foreign.Where(m => m.Remote.Id != o3.Id), preview.Calendars);
        Assert.Equal(1, result.Deleted);
        Assert.Single(result.Skipped, s => s.Contains("changed since the scan"));
        Assert.DoesNotContain(o1.Id, e.G.Events["uni"].Keys);
        Assert.Contains(o2.Id, e.G.Events["uni"].Keys);
        Assert.Contains(o3.Id, e.G.Events["uni"].Keys);
        Assert.Contains(dentist.Id, e.G.Events["uni"].Keys);
    }

    [Fact]
    public async Task Cleanup_refuses_read_only_calendars_and_our_own_events()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.Save();   // s1 exists as an old import in "ro", s2 becomes ours
        var ro = e.G.AddForeign("ro", "010004-1 PS Einführung", At(2, 3), At(2, 3).AddMinutes(90));
        e.Settings.GoogleScanCalendarIds = ["ro"];
        await e.Sync();
        var preview = await e.Svc.PreviewAsync();

        var ours = e.G.Events["main"].Values.First();
        var fakeMatches = preview.Foreign.Concat([new ForeignMatch(ours, "session:s1", MatchConfidence.Strong, "x")]).ToList();
        var result = await e.Svc.DeleteForeignAsync(fakeMatches, preview.Calendars);
        Assert.Equal(0, result.Deleted);
        Assert.Contains(result.Skipped, s => s.Contains("read-only"));
        Assert.Contains(result.Skipped, s => s.Contains("created by this app"));
        Assert.Contains(ro.Id, e.G.Events["ro"].Keys);
        Assert.Contains(ours.Id, e.G.Events["main"].Keys);
    }

    [Fact]
    public async Task Id_collision_with_a_foreign_event_is_reported_and_the_foreign_event_is_untouched()
    {
        var e = new Env();
        var s = e.AddSession("s1", At(2, 3)); e.Save();
        var id = ExportIds.GoogleEventId("session:s1");
        var foreign = new RemoteEvent("main", "Main", id, null, "Something else", At(2, 3), At(2, 3).AddHours(1), false, null, null, "\"x1\"", null);
        e.G.Events["main"][id] = foreign;

        var r = await e.Sync();
        Assert.Equal(0, r.Created);
        Assert.Single(r.Errors);
        Assert.Equal(foreign, e.G.Events["main"][id]);
    }

    [Fact]
    public async Task Remove_all_deletes_every_event_this_app_created_and_nothing_else()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.AddSession("s2", At(2, 10)); e.Save();
        var dentist = e.G.AddForeign("main", "Dentist", At(3, 3), At(3, 3).AddHours(1));
        await e.Sync();
        var r = await e.Svc.RemoveAllManagedAsync();
        Assert.Equal(2, r.Deleted);
        Assert.Equal([dentist.Id], e.G.Events["main"].Keys.ToArray());
        Assert.Empty(e.Db.GetGoogleState());
    }

    [Fact]
    public async Task Read_only_target_and_missing_target_are_rejected()
    {
        var e = new Env("ro");
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Svc.PreviewAsync());
        var none = new Env("");
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.Svc.PreviewAsync());
    }

    [Fact]
    public async Task Preview_never_writes()
    {
        var e = new Env();
        e.AddSession("s1", At(2, 3)); e.Save();
        var p = await e.Svc.PreviewAsync();
        Assert.Equal(1, p.Plan.Count(SyncActionKind.Create));
        Assert.Equal(0, e.G.Writes);
        Assert.Empty(e.Db.GetGoogleState());
    }

    [Fact]
    public async Task Registered_exam_without_time_becomes_an_all_day_event()
    {
        var e = new Env();
        e.Save();
        e.Db.SaveEnrolment(e.Enr, [], null, null, [new ExamRecord
        {
            Key = Key + "|x1", EnrolmentKey = Key, ExtId = "x1", Title = "Einführung", Begin = At(3, 8, 0), End = At(3, 8, 0), TimeKnown = false, Registered = true,
            FirstSeenAt = Now, LastChangedAt = Now, Hash = "h",
        }]);
        await e.Sync();
        var ev = e.G.Events["main"].Values.Single();
        Assert.True(ev.AllDay);
        Assert.StartsWith("Exam: ", ev.Summary);
    }
}

// ============================================================ REST client (fake HTTP)

public class GoogleCalendarClientTests
{
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, Calls.Count);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        tweak?.Invoke(r);
        return r;
    }

    static GoogleCalendarClient Client(Handler h, List<bool>? tokenForced = null, List<TimeSpan>? delays = null) =>
        new(new HttpClient(h), (forced, _) => { tokenForced?.Add(forced); return Task.FromResult(forced ? "T2" : "T1"); },
            (t, _) => { delays?.Add(t); return Task.CompletedTask; });

    static GoogleEventData Data(bool allDay = false) => new("mdabc123", "Algebra (VO)", "HS 1", "desc", new DateTimeOffset(2099, 2, 3, 9, 0, 0, TimeSpan.FromHours(1)),
        new DateTimeOffset(2099, 2, 3, 10, 30, 0, TimeSpan.FromHours(1)), allDay, "https://ufind.univie.ac.at/x", "session:k", "hash1");

    const string OneEvent = """{"id":"mdabc123","etag":"\"e1\"","summary":"Algebra (VO)","updated":"2099-01-01T00:00:00Z","start":{"dateTime":"2099-02-03T09:00:00+01:00"},"end":{"dateTime":"2099-02-03T10:30:00+01:00"},"extendedProperties":{"private":{"md_managed":"1","md_key":"session:k","md_hash":"hash1"}}}""";

    [Fact]
    public async Task Insert_sends_marker_explicit_id_vienna_time_and_no_notifications()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, OneEvent));
        var r = await Client(h).InsertAsync("main", "Main", Data(), default);
        var (req, body) = h.Calls.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("sendUpdates=none", req.RequestUri!.Query);
        Assert.Equal("Bearer T1", req.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("mdabc123", root.GetProperty("id").GetString());
        Assert.Equal("1", root.GetProperty("extendedProperties").GetProperty("private").GetProperty("md_managed").GetString());
        Assert.Equal("session:k", root.GetProperty("extendedProperties").GetProperty("private").GetProperty("md_key").GetString());
        Assert.Equal("Europe/Vienna", root.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.StartsWith("2099-02-03T09:00:00", root.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.True(r.Managed);
        Assert.Equal("\"e1\"", r.Etag);
    }

    [Fact]
    public async Task All_day_events_use_dates()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, OneEvent));
        await Client(h).InsertAsync("main", "Main", Data(allDay: true), default);
        using var doc = JsonDocument.Parse(h.Calls.Single().Body);
        Assert.Equal("2099-02-03", doc.RootElement.GetProperty("start").GetProperty("date").GetString());
        Assert.False(doc.RootElement.GetProperty("start").TryGetProperty("dateTime", out _));
        Assert.Equal("transparent", doc.RootElement.GetProperty("transparency").GetString());
    }

    [Fact]
    public async Task Patch_and_delete_carry_if_match_and_patch_has_no_id_in_body()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, OneEvent));
        var c = Client(h);
        await c.PatchAsync("main", "Main", "mdabc123", Data(), "\"e1\"", default);
        Assert.Equal("PATCH", h.Calls[0].Req.Method.Method);
        Assert.Equal("\"e1\"", h.Calls[0].Req.Headers.GetValues("If-Match").Single());
        Assert.False(JsonDocument.Parse(h.Calls[0].Body).RootElement.TryGetProperty("id", out _));

        var h2 = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        await Client(h2).DeleteAsync("main", "mdabc123", "\"e2\"", default);
        Assert.Equal(HttpMethod.Delete, h2.Calls[0].Req.Method);
        Assert.Equal("\"e2\"", h2.Calls[0].Req.Headers.GetValues("If-Match").Single());
        Assert.Contains("sendUpdates=none", h2.Calls[0].Req.RequestUri!.Query);
    }

    [Fact]
    public async Task Error_statuses_map_to_typed_exceptions()
    {
        static Handler Status(HttpStatusCode c) => new((_, _) => Json(c, """{"error":{"code":0,"message":"nope"}}"""));
        await Assert.ThrowsAsync<GoogleConflictException>(() => Client(Status(HttpStatusCode.Conflict)).InsertAsync("m", "M", Data(), default));
        await Assert.ThrowsAsync<GooglePreconditionException>(() => Client(Status(HttpStatusCode.PreconditionFailed)).PatchAsync("m", "M", "i", Data(), "e", default));
        await Assert.ThrowsAsync<GoogleNotFoundException>(() => Client(Status(HttpStatusCode.NotFound)).DeleteAsync("m", "i", null, default));
        await Assert.ThrowsAsync<GoogleNotFoundException>(() => Client(Status(HttpStatusCode.Gone)).DeleteAsync("m", "i", null, default));
        Assert.Null(await Client(Status(HttpStatusCode.NotFound)).GetAsync("m", "M", "i", default));
    }

    [Fact]
    public async Task Unauthorized_triggers_exactly_one_token_refresh_and_retry()
    {
        var forced = new List<bool>();
        var h = new Handler((_, n) => n == 1 ? Json(HttpStatusCode.Unauthorized, "{}") : Json(HttpStatusCode.OK, OneEvent));
        await Client(h, forced).InsertAsync("main", "Main", Data(), default);
        Assert.Equal([false, true], forced);
        Assert.Equal("Bearer T2", h.Calls[1].Req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Rate_limits_and_server_errors_are_retried_with_backoff_honouring_retry_after()
    {
        var delays = new List<TimeSpan>();
        var h = new Handler((_, n) => n switch
        {
            1 => Json((HttpStatusCode)429, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(7))),
            2 => Json(HttpStatusCode.Forbidden, """{"error":{"message":"x","errors":[{"reason":"rateLimitExceeded"}]}}"""),
            3 => Json(HttpStatusCode.BadGateway, "{}"),
            _ => Json(HttpStatusCode.OK, OneEvent),
        });
        await Client(h, delays: delays).InsertAsync("main", "Main", Data(), default);
        Assert.Equal(4, h.Calls.Count);
        Assert.Equal(3, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), delays[0]);
    }

    [Fact]
    public async Task Plain_forbidden_is_not_retried()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.Forbidden, """{"error":{"message":"insufficient scope","errors":[{"reason":"forbidden"}]}}"""));
        var ex = await Assert.ThrowsAsync<GoogleApiException>(() => Client(h).InsertAsync("main", "Main", Data(), default));
        Assert.Equal(403, ex.Status);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Event_listing_follows_page_tokens_and_parses_markers_all_day_and_cancelled()
    {
        var h = new Handler((req, n) => n == 1
            ? Json(HttpStatusCode.OK, """{"items":[{"id":"a","summary":"One","start":{"date":"2099-03-01"},"end":{"date":"2099-03-02"},"etag":"\"1\""}],"nextPageToken":"P2"}""")
            : Json(HttpStatusCode.OK, """{"items":[{"id":"b","summary":"Two","status":"cancelled","start":{"dateTime":"2099-03-02T10:00:00+01:00"},"end":{"dateTime":"2099-03-02T11:00:00+01:00"}},""" +
                                       """{"id":"c","summary":"Three","start":{"dateTime":"2099-03-03T10:00:00+01:00"},"end":{"dateTime":"2099-03-03T11:00:00+01:00"},"extendedProperties":{"private":{"md_managed":"1","md_key":"k"}}}]}"""));
        var list = await Client(h).ListEventsAsync("cal@x.com", "Cal", new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 4, 1, 0, 0, 0, TimeSpan.Zero), default);
        Assert.Equal(3, list.Count);
        Assert.Contains("pageToken=P2", h.Calls[1].Req.RequestUri!.Query);
        Assert.Contains("cal%40x.com", h.Calls[0].Req.RequestUri!.AbsolutePath);
        Assert.Contains("singleEvents=true", h.Calls[0].Req.RequestUri!.Query);
        Assert.True(list[0].AllDay);
        Assert.True(list[1].Cancelled);
        Assert.True(list[2].Managed);
        Assert.Equal("k", list[2].ManagedKey);
    }

    [Fact]
    public async Task Managed_listing_filters_server_side_by_the_private_marker()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, """{"items":[]}"""));
        await Client(h).ListManagedEventsAsync("main", "Main", default);
        var q = Uri.UnescapeDataString(h.Calls.Single().Req.RequestUri!.Query);
        Assert.Contains("privateExtendedProperty=md_managed=1", q);
    }

    [Fact]
    public async Task Calendar_list_maps_roles()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, """{"items":[{"id":"a","summary":"A","accessRole":"owner","primary":true},{"id":"b","summary":"B","summaryOverride":"Mine","accessRole":"reader"}]}"""));
        var cals = await Client(h).ListCalendarsAsync(default);
        Assert.True(cals[0].CanWrite);
        Assert.True(cals[0].Primary);
        Assert.False(cals[1].CanWrite);
        Assert.Equal("Mine", cals[1].Name);
    }
}

// ============================================================ OAuth

public class GoogleAuthTests
{
    sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return respond(request, body);
        }
    }

    static (GoogleAuth Auth, GoogleCredentialStore Store, Handler H) Make(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var store = new GoogleCredentialStore(Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid()}.bin"));
        store.SaveClient("client-id", "client-secret");
        var h = new Handler(respond);
        return (new GoogleAuth(new HttpClient(h), store), store, h);
    }

    static HttpResponseMessage Json(HttpStatusCode c, string j) => new(c) { Content = new StringContent(j, Encoding.UTF8, "application/json") };

    [Fact]
    public void Pkce_challenge_matches_the_rfc_7636_test_vector() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Verifiers_are_random_and_long_enough()
    {
        var a = GoogleAuth.NewVerifier();
        Assert.InRange(a.Length, 43, 128);
        Assert.NotEqual(a, GoogleAuth.NewVerifier());
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public void Auth_url_carries_pkce_offline_access_and_only_the_two_calendar_scopes()
    {
        var (auth, _, _) = Make((_, _) => Json(HttpStatusCode.OK, "{}"));
        var url = auth.BuildAuthUrl("client-id", "http://127.0.0.1:5555/", "st", "chal");
        Assert.Contains("code_challenge=chal", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5555%2F", url);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/calendar.events"), url);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/calendar.calendarlist.readonly"), url);
        Assert.DoesNotContain("auth/calendar ", url);
    }

    [Fact]
    public async Task Code_exchange_stores_the_refresh_token_and_caches_the_access_token()
    {
        var (auth, store, h) = Make((_, _) => Json(HttpStatusCode.OK, """{"access_token":"AT1","expires_in":3600,"refresh_token":"RT1"}"""));
        await auth.ExchangeCodeAsync("code123", "http://127.0.0.1:1/", "verifier");
        Assert.Contains("code_verifier=verifier", h.Bodies[0]);
        Assert.Contains("grant_type=authorization_code", h.Bodies[0]);
        Assert.Equal("RT1", store.Load().RefreshToken);
        Assert.True(auth.IsConnected);
        Assert.Equal("AT1", await auth.GetAccessTokenAsync());
        Assert.Single(h.Bodies);                                  // cached: no refresh call
    }

    [Fact]
    public async Task Refresh_uses_the_stored_refresh_token_and_force_bypasses_the_cache()
    {
        var n = 0;
        var (auth, store, h) = Make((_, _) => Json(HttpStatusCode.OK, $$"""{"access_token":"AT{{++n}}","expires_in":3600}"""));
        store.SaveRefreshToken("RT");
        Assert.Equal("AT1", await auth.GetAccessTokenAsync());
        Assert.Equal("AT1", await auth.GetAccessTokenAsync());
        Assert.Equal("AT2", await auth.GetAccessTokenAsync(forceRefresh: true));
        Assert.Contains("grant_type=refresh_token", h.Bodies[0]);
        Assert.Contains("refresh_token=RT", h.Bodies[0]);
    }

    [Fact]
    public async Task Invalid_grant_clears_the_token_and_asks_to_reconnect()
    {
        var (auth, store, _) = Make((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));
        store.SaveRefreshToken("RT");
        await Assert.ThrowsAsync<GoogleAuthRequiredException>(() => auth.GetAccessTokenAsync());
        Assert.False(auth.IsConnected);
    }

    [Fact]
    public async Task Missing_refresh_token_in_the_code_response_is_an_error()
    {
        var (auth, _, _) = Make((_, _) => Json(HttpStatusCode.OK, """{"access_token":"AT","expires_in":3600}"""));
        await Assert.ThrowsAsync<GoogleAuthRequiredException>(() => auth.ExchangeCodeAsync("c", "http://127.0.0.1:1/", "v"));
    }

    [Fact]
    public async Task Disconnect_forgets_the_token_and_tries_to_revoke_it()
    {
        var (auth, store, h) = Make((_, _) => Json(HttpStatusCode.OK, "{}"));
        store.SaveRefreshToken("RT");
        await auth.DisconnectAsync();
        Assert.False(auth.IsConnected);
        Assert.Contains("token=RT", h.Bodies.Single());
    }

    [Fact]
    public void Credentials_are_encrypted_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid()}.bin");
        var store = new GoogleCredentialStore(path);
        store.SaveClient("my-client-id", "my-secret");
        store.SaveRefreshToken("my-refresh");
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("my-secret", raw);
        Assert.DoesNotContain("my-refresh", raw);
        var back = store.Load();
        Assert.Equal(("my-client-id", "my-secret", "my-refresh"), (back.ClientId, back.ClientSecret, back.RefreshToken));
    }
}
