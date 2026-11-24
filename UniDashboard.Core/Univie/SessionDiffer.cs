using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Univie;

/// <summary>
/// Compares stored sessions of one enrolment with freshly fetched ones and decides, per session:
/// unchanged / changed in place (time or room) / moved to another slot / removed (tombstone kept) / added.
/// Identity (no id from the source): enrolment key + local date + ordinal among that day's sessions.
/// Pairing of leftover removed+added sessions as "moved" is a heuristic (see <see cref="Diff"/>).
/// </summary>
public static class SessionDiffer
{
    public static string KeyFor(string enrolmentKey, DateTimeOffset begin, int ordinal) =>
        $"{enrolmentKey}|{begin.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{ordinal}";

    public static string HashOf(DateTimeOffset begin, DateTimeOffset end, string? room)
    {
        var s = $"{begin.ToUnixTimeSeconds()}|{end.ToUnixTimeSeconds()}|{(room ?? "").Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    /// <summary>Fresh sessions → records with derived keys (ordinal per local day, ordered by start).</summary>
    public static List<SessionRecord> ToRecords(string enrolmentKey, IEnumerable<SessionDto> fresh, DateTimeOffset now)
    {
        var list = new List<SessionRecord>();
        foreach (var day in fresh.OrderBy(s => s.Begin).GroupBy(s => s.Begin.ToLocalTime().Date))
        {
            var i = 0;
            foreach (var s in day)
                list.Add(new()
                {
                    Key = KeyFor(enrolmentKey, s.Begin, i++), EnrolmentKey = enrolmentKey, Begin = s.Begin, End = s.End, Room = s.Room,
                    Hash = HashOf(s.Begin, s.End, s.Room), FirstSeenAt = now, LastChangedAt = now,
                });
        }
        return list;
    }

    /// <param name="old">All stored rows of the enrolment (active and tombstones).</param>
    /// <param name="fresh">Freshly fetched sessions.</param>
    /// <param name="first">True when the enrolment was never synced before: everything is "added" silently.</param>
    public static (List<SessionRecord> Result, List<SessionEvent> Events) Diff(
        IReadOnlyList<SessionRecord> old, string enrolmentKey, IEnumerable<SessionDto> fresh, DateTimeOffset now, bool first = false)
    {
        var news = ToRecords(enrolmentKey, fresh, now);
        var oldActive = old.Where(o => o.Status == SessionStatus.Active).ToDictionary(o => o.Key);
        var result = new List<SessionRecord>();
        var events = new List<SessionEvent>();

        var leftoverNew = new List<SessionRecord>();
        foreach (var n in news)
        {
            if (!oldActive.Remove(n.Key, out var o)) { leftoverNew.Add(n); continue; }
            if (o.Hash == n.Hash)
            {
                // unchanged: keep history labels (moved/changed) and first-seen time
                n.FirstSeenAt = o.FirstSeenAt; n.LastChangedAt = o.LastChangedAt; n.Change = o.Change;
                n.PrevBegin = o.PrevBegin; n.PrevEnd = o.PrevEnd; n.PrevRoom = o.PrevRoom; n.PrevKey = o.PrevKey;
                result.Add(n);
            }
            else
            {
                n.FirstSeenAt = o.FirstSeenAt; n.Change = SessionChange.Changed;
                n.PrevBegin = o.Begin; n.PrevEnd = o.End; n.PrevRoom = o.Room;
                result.Add(n);
                events.Add(new(SessionChange.Changed, n, o));
            }
        }

        // Leftovers: removed in the old set, added in the new one → pair as "moved" when plausible.
        var leftoverOld = oldActive.Values.OrderBy(o => o.Begin).ToList();
        leftoverNew = leftoverNew.OrderBy(n => n.Begin).ToList();
        var pairs = new List<(SessionRecord Old, SessionRecord New)>();

        if (!first && leftoverOld.Count > 0 && leftoverNew.Count > 0)
        {
            if (leftoverOld.Count == leftoverNew.Count)
                for (var i = 0; i < leftoverOld.Count; i++) pairs.Add((leftoverOld[i], leftoverNew[i]));
            else
            {
                // Different counts: greedily pair the closest starts with a similar duration (±30 min).
                var cands = (from o in leftoverOld from n in leftoverNew
                             where Math.Abs(((o.End - o.Begin) - (n.End - n.Begin)).TotalMinutes) <= 30
                             orderby Math.Abs((o.Begin - n.Begin).TotalMinutes)
                             select (o, n)).ToList();
                var usedO = new HashSet<string>(); var usedN = new HashSet<string>();
                foreach (var (o, n) in cands)
                {
                    if (usedO.Contains(o.Key) || usedN.Contains(n.Key)) continue;
                    usedO.Add(o.Key); usedN.Add(n.Key); pairs.Add((o, n));
                }
            }
        }

        var pairedOld = pairs.Select(p => p.Old.Key).ToHashSet();
        var pairedNew = pairs.Select(p => p.New.Key).ToHashSet();

        foreach (var (o, n) in pairs)
        {
            n.FirstSeenAt = o.FirstSeenAt; n.Change = SessionChange.Moved;
            n.PrevBegin = o.Begin; n.PrevEnd = o.End; n.PrevRoom = o.Room; n.PrevKey = o.Key;
            result.Add(n);
            events.Add(new(SessionChange.Moved, n, o));
        }
        foreach (var n in leftoverNew.Where(n => !pairedNew.Contains(n.Key)))
        {
            n.Change = first ? SessionChange.None : SessionChange.Added;
            result.Add(n);
            if (!first) events.Add(new(SessionChange.Added, n, null));
        }
        foreach (var o in leftoverOld.Where(o => !pairedOld.Contains(o.Key)))
        {
            o.Status = SessionStatus.Removed; o.Change = SessionChange.Removed; o.LastChangedAt = now;
            result.Add(o);
            events.Add(new(SessionChange.Removed, o, o));
        }

        // Keep existing tombstones (unless the same slot is active again), purge old ones.
        var activeKeys = result.Select(r => r.Key).ToHashSet();
        foreach (var t in old.Where(o => o.Status == SessionStatus.Removed && !activeKeys.Contains(o.Key) && o.Begin > now.AddDays(-7)))
            result.Add(t);

        return (result.OrderBy(r => r.Begin).ToList(), events);
    }
}
