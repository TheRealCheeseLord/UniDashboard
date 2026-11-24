using System.Security.Cryptography;
using System.Text;

namespace UniDashboard.Core.Univie;

public enum ExamStatus { Active = 0, Removed = 1 }
public enum ExamChange { None = 0, Added = 1, Changed = 2, Removed = 3 }

/// <summary>One possible (or registered) exam date of an enrolled course. Identity = u:find <c>extid</c>.</summary>
public class ExamRecord
{
    public string Key { get; set; } = "";             // enrolmentKey|extid
    public string EnrolmentKey { get; set; } = "";
    public string ExtId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset Begin { get; set; }
    public DateTimeOffset End { get; set; }
    public bool TimeKnown { get; set; }
    public string? When { get; set; }
    public string? Room { get; set; }
    public DateTimeOffset? EnrollFrom { get; set; }   // registration window (null: not published yet)
    public DateTimeOffset? EnrollTo { get; set; }
    public DateTimeOffset? DisenrollUntil { get; set; }
    public bool Registered { get; set; }              // from u:space
    public string Hash { get; set; } = "";
    public ExamStatus Status { get; set; } = ExamStatus.Active;
    public ExamChange Change { get; set; } = ExamChange.None;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    public DateTimeOffset? PrevBegin { get; set; }
    public DateTimeOffset? PrevEnrollFrom { get; set; }
    public DateTimeOffset? PrevEnrollTo { get; set; }

    public bool HasWindow => EnrollFrom is not null && EnrollTo is not null;
    public bool WindowOpen(DateTimeOffset now) => HasWindow && now >= EnrollFrom && now <= EnrollTo;

    /// <summary>Everything except the registered flag (registering yourself is not a change of the exam).</summary>
    public static string HashOf(ExamRecord e)
    {
        string T(DateTimeOffset? d) => d?.ToUnixTimeSeconds().ToString() ?? "-";
        var s = string.Join("|", T(e.Begin), T(e.End), e.TimeKnown, e.Title, e.When, T(e.EnrollFrom), T(e.EnrollTo), T(e.DisenrollUntil));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }
}

public record ExamEvent(ExamChange Change, ExamRecord Exam, ExamRecord? Previous);

/// <summary>Compares stored exam dates of one enrolment with freshly fetched ones by their stable extid.</summary>
public static class ExamDiffer
{
    public static (List<ExamRecord> Result, List<ExamEvent> Events) Diff(
        IReadOnlyList<ExamRecord> old, IEnumerable<ExamRecord> fresh, DateTimeOffset now, bool first = false)
    {
        var oldActive = old.Where(o => o.Status == ExamStatus.Active).ToDictionary(o => o.Key);
        var result = new List<ExamRecord>();
        var events = new List<ExamEvent>();

        foreach (var n in fresh)
        {
            n.Hash = ExamRecord.HashOf(n);
            n.FirstSeenAt = now; n.LastChangedAt = now;
            if (!oldActive.Remove(n.Key, out var o))
            {
                n.Change = first ? ExamChange.None : ExamChange.Added;
                if (!first) events.Add(new(ExamChange.Added, n, null));
            }
            else
            {
                n.FirstSeenAt = o.FirstSeenAt;
                if (o.Hash == n.Hash)
                {   // unchanged: keep earlier change label + history
                    n.LastChangedAt = o.LastChangedAt; n.Change = o.Change;
                    n.PrevBegin = o.PrevBegin; n.PrevEnrollFrom = o.PrevEnrollFrom; n.PrevEnrollTo = o.PrevEnrollTo;
                }
                else
                {
                    n.Change = ExamChange.Changed;
                    n.PrevBegin = o.Begin; n.PrevEnrollFrom = o.EnrollFrom; n.PrevEnrollTo = o.EnrollTo;
                    events.Add(new(ExamChange.Changed, n, o));
                }
            }
            result.Add(n);
        }

        foreach (var gone in oldActive.Values)
        {
            gone.Status = ExamStatus.Removed; gone.Change = ExamChange.Removed; gone.LastChangedAt = now;
            result.Add(gone);
            events.Add(new(ExamChange.Removed, gone, gone));
        }

        // Keep existing tombstones for a week after the exam date.
        var keys = result.Select(r => r.Key).ToHashSet();
        foreach (var t in old.Where(o => o.Status == ExamStatus.Removed && !keys.Contains(o.Key) && o.Begin > now.AddDays(-7)))
            result.Add(t);

        return (result.OrderBy(r => r.Begin).ToList(), events);
    }
}
