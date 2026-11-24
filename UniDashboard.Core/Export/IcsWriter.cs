using System.Globalization;
using System.Text;

namespace UniDashboard.Core.Export;

/// <summary>
/// RFC 5545 writer. Stable UIDs (derived from the item key) + a SEQUENCE that rises whenever an item's content changes make
/// re-importing the file update the existing events instead of duplicating them.
/// </summary>
public static class IcsWriter
{
    const string Crlf = "\r\n";

    /// <param name="sequenceOf">Revision counter per item (0 for never exported); see <see cref="NextSequences"/>.</param>
    /// <param name="alarmMinutes">0 = no reminder; otherwise a display alarm that many minutes before timed events.</param>
    public static string Write(IEnumerable<ExportItem> items, Func<ExportItem, int> sequenceOf, DateTimeOffset now, int alarmMinutes = 0, string calendarName = "Uni (UniDashboard)")
    {
        var sb = new StringBuilder();
        void Line(string s) => Fold(sb, s);

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//UniDashboard//Export 1.0//EN");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:" + Escape(calendarName));
        Line("X-WR-TIMEZONE:Europe/Vienna");

        foreach (var i in items)
        {
            Line("BEGIN:VEVENT");
            Line("UID:" + ExportIds.Uid(i.Key));
            Line("DTSTAMP:" + Utc(now));
            Line("SEQUENCE:" + sequenceOf(i));
            if (i.AllDay)
            {
                Line("DTSTART;VALUE=DATE:" + Date(i.Start));
                Line("DTEND;VALUE=DATE:" + Date(i.End));
                Line("TRANSP:TRANSPARENT");
            }
            else
            {
                Line("DTSTART:" + Utc(i.Start));
                Line("DTEND:" + Utc(i.End));
            }
            Line("SUMMARY:" + Escape(i.Title));
            if (!string.IsNullOrWhiteSpace(i.Location)) Line("LOCATION:" + Escape(i.Location!));
            if (!string.IsNullOrWhiteSpace(i.Description)) Line("DESCRIPTION:" + Escape(i.Description));
            if (!string.IsNullOrWhiteSpace(i.Url)) Line("URL:" + i.Url);
            Line("CATEGORIES:" + (i.Kind == ExportKind.Exam ? "Exam" : "Uni"));
            Line("X-UNIDASHBOARD-KEY:" + Escape(i.Key));
            if (alarmMinutes > 0 && !i.AllDay)
            {
                Line("BEGIN:VALARM");
                Line("ACTION:DISPLAY");
                Line("DESCRIPTION:" + Escape(i.Title));
                Line($"TRIGGER:-PT{alarmMinutes}M");
                Line("END:VALARM");
            }
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>
    /// Computes the SEQUENCE for each item against the stored state: unchanged → same number, changed → +1, new → 0.
    /// Returns the sequences plus the rows to persist.
    /// </summary>
    public static (Dictionary<string, int> Sequences, List<(string Key, string Hash, int Seq)> ToSave) NextSequences(
        IEnumerable<ExportItem> items, IReadOnlyDictionary<string, (string Hash, int Seq)> state)
    {
        var seqs = new Dictionary<string, int>();
        var save = new List<(string, string, int)>();
        foreach (var i in items)
        {
            var h = i.Hash;
            if (state.TryGetValue(i.Key, out var old))
            {
                var seq = old.Hash == h ? old.Seq : old.Seq + 1;
                seqs[i.Key] = seq;
                if (old.Hash != h) save.Add((i.Key, h, seq));
            }
            else { seqs[i.Key] = 0; save.Add((i.Key, h, 0)); }
        }
        return (seqs, save);
    }

    // ---------- helpers

    public static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>All-day dates are Vienna dates (the university's calendar), not the dates of whatever timezone this PC is set to.</summary>
    static string Date(DateTimeOffset t) => UniDashboard.Core.Google.ViennaTime.DateString(t).Replace("-", "");

    /// <summary>Folds a content line at 75 octets (never splitting a UTF-8 sequence); continuation lines start with one space.</summary>
    static void Fold(StringBuilder sb, string line)
    {
        var limit = 75;
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > limit)
            {
                sb.Append(Crlf).Append(' ');
                bytes = 1;          // the leading space counts as an octet of the continuation line
                limit = 75;
            }
            sb.Append(rune.ToString());
            bytes += n;
        }
        sb.Append(Crlf);
    }
}
