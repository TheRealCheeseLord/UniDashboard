using System.Text.RegularExpressions;

namespace MoodleDashboard.Core.Export;

/// <summary>An event as read from Google Calendar (or any calendar), reduced to what the matcher and the planner need.</summary>
public record RemoteEvent(
    string CalendarId, string CalendarName, string Id, string? ICalUid, string Summary,
    DateTimeOffset? Start, DateTimeOffset? End, bool AllDay, string? Location, string? Description,
    string? Etag, string? Updated, bool Cancelled = false,
    bool Managed = false, string? ManagedKey = null, string? ManagedHash = null);

public enum MatchConfidence { Probable, Strong }

public record ForeignMatch(RemoteEvent Remote, string ItemKey, MatchConfidence Confidence, string Reason);

/// <summary>
/// Recognises events the user imported earlier (u:find's iCal link, u:space "add to calendar", …) so that we neither
/// duplicate them nor touch them. u:find iCal events have random UIDs, so recognition is by pattern, never by UID:
///   strong   — SUMMARY "{lvnr}-{group} {type} {title}" with an enrolled lvnr/group and the same start/end, or a u:find course
///              link for the same lvnr in the description with the same start
///   probable — the summary contains the course title or lvnr and the start (and end) match
/// Events carrying our own marker are never considered (they are ours).
/// </summary>
public static partial class ImportedEventMatcher
{
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"^\s*(\d{6})-(\d+)\s+\S+\s+.+$")]
    private static partial Regex UfindSummary();

    [GeneratedRegex(@"ufind\.univie\.ac\.at/[a-z]{2}/course\.html\?lv=(\d{6})", RegexOptions.IgnoreCase)]
    private static partial Regex UfindLink();

    public static List<ForeignMatch> Match(IEnumerable<RemoteEvent> remote, IReadOnlyList<ExportItem> desired)
    {
        // Index desired items by start minute for ±tolerance lookups.
        var byMinute = new Dictionary<long, List<ExportItem>>();
        foreach (var i in desired)
        {
            var m = i.Start.ToUnixTimeSeconds() / 60;
            if (!byMinute.TryGetValue(m, out var l)) byMinute[m] = l = [];
            l.Add(i);
        }

        var result = new List<ForeignMatch>();
        foreach (var r in remote)
        {
            if (r.Managed || r.Cancelled || r.Start is null) continue;
            var candidates = Near(byMinute, r.Start.Value).Where(i => EndsAgree(i, r)).ToList();
            if (candidates.Count == 0) continue;

            var m = UfindSummary().Match(r.Summary ?? "");
            if (m.Success)
            {
                var hit = candidates.FirstOrDefault(i => i.Lvnr == m.Groups[1].Value && i.GroupNr == m.Groups[2].Value);
                if (hit is not null) { result.Add(new(r, hit.Key, MatchConfidence.Strong, "u:find iCal title pattern, same time")); continue; }
            }

            var link = UfindLink().Match((r.Description ?? "") + " " + (r.Location ?? ""));
            if (link.Success && candidates.FirstOrDefault(i => i.Lvnr == link.Groups[1].Value) is { } byLink)
            { result.Add(new(r, byLink.Key, MatchConfidence.Strong, "u:find course link, same time")); continue; }

            var summary = (r.Summary ?? "").ToLowerInvariant();
            var probable = candidates.FirstOrDefault(i =>
                (!string.IsNullOrEmpty(i.CourseTitle) && summary.Contains(i.CourseTitle!.ToLowerInvariant())) ||
                (!string.IsNullOrEmpty(i.Lvnr) && summary.Contains(i.Lvnr!)));
            if (probable is not null) result.Add(new(r, probable.Key, MatchConfidence.Probable, "title or course number and time match"));
        }
        return result;
    }

    static IEnumerable<ExportItem> Near(Dictionary<long, List<ExportItem>> byMinute, DateTimeOffset start)
    {
        var m = start.ToUnixTimeSeconds() / 60;
        for (var d = -(long)Tolerance.TotalMinutes - 1; d <= (long)Tolerance.TotalMinutes + 1; d++)
            if (byMinute.TryGetValue(m + d, out var l))
                foreach (var i in l)
                    if ((i.Start - start).Duration() <= Tolerance) yield return i;
    }

    // All-day items only need the start date; timed ones need the end to agree too (when the remote event has one).
    static bool EndsAgree(ExportItem i, RemoteEvent r) =>
        i.AllDay || r.End is null || (i.End - r.End.Value).Duration() <= Tolerance;
}
