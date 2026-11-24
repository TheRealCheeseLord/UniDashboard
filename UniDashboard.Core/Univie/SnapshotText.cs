using System.Globalization;
using System.Text.Json;
using UniDashboard.Core.Docx;

namespace UniDashboard.Core.Univie;

/// <summary>Turns an enrolment snapshot (JSON) into diffable plain text for the history view.</summary>
public static class SnapshotText
{
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    public static string Render(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var lines = new List<string>
        {
            "Title: " + S(r, "Title"),
            $"Type: {S(r, "Type")}   Semester: {S(r, "Semester")}",
            "Status: " + S(r, "Status") + (r.TryGetProperty("WaitlistPosition", out var w) && w.ValueKind == JsonValueKind.Number ? $" (waitlist #{w.GetInt32()})" : ""),
        };
        if (S(r, "Remark") is { Length: > 0 } rem) lines.Add("Remark: " + rem);
        if (r.TryGetProperty("Lecturers", out var le) && le.ValueKind == JsonValueKind.Array)
            lines.Add("Lecturers: " + string.Join(", ", le.EnumerateArray().Select(l => $"{S(l, "FirstName")} {S(l, "LastName")}".Trim())));

        if (r.TryGetProperty("Exams", out var ex) && ex.ValueKind == JsonValueKind.Array && ex.GetArrayLength() > 0)
        {
            lines.Add("Exams:");
            foreach (var x in ex.EnumerateArray())
            {
                var b = DateTimeOffset.FromUnixTimeSeconds(x.GetProperty("Begin").GetInt64()).ToLocalTime();
                var tk = x.TryGetProperty("TimeKnown", out var t) && t.ValueKind == JsonValueKind.True;
                string W(string n) => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()).ToLocalTime().ToString("d.M. HH:mm", De) : "–";
                lines.Add($"  {b.ToString(tk ? "ddd d.M.yyyy HH:mm" : "ddd d.M.yyyy", De)}  register {W("EnrollFrom")} → {W("EnrollTo")}, deregister until {W("DisenrollUntil")}" +
                          (x.TryGetProperty("Registered", out var rg) && rg.ValueKind == JsonValueKind.True ? "  [REGISTERED]" : "") +
                          (x.TryGetProperty("Status", out var st) && st.GetString() == "Removed" ? "  [REMOVED]" : ""));
            }
        }

        lines.Add("Sessions:");
        if (r.TryGetProperty("Sessions", out var se) && se.ValueKind == JsonValueKind.Array)
            foreach (var s in se.EnumerateArray())
            {
                var b = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("Begin").GetInt64()).ToLocalTime();
                var e = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("End").GetInt64()).ToLocalTime();
                var status = S(s, "Status");
                lines.Add($"  {b.ToString("ddd d.M.yyyy HH:mm", De)}–{e:HH:mm}  {S(s, "Room")}" + (status == "Removed" ? "  [CANCELLED]" : ""));
            }

        if (r.TryGetProperty("Ufind", out var uf) && uf.ValueKind == JsonValueKind.Object)
        {
            lines.Add($"ECTS: {(uf.TryGetProperty("Ects", out var ects) ? ects.ToString() : "")}   SWS: {(uf.TryGetProperty("Sws", out var sws) ? sws.ToString() : "")}");
            if (uf.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                    foreach (var field in new[] { "Comment", "Performance", "Examination", "Preconditions", "Literature" })
                        if (g.TryGetProperty(field, out var d) && d.ValueKind == JsonValueKind.Object && d.EnumerateObject().Any())
                        {
                            var text = d.TryGetProperty("de", out var de) ? de.GetString() : d.EnumerateObject().FirstOrDefault().Value.GetString();
                            if (!string.IsNullOrWhiteSpace(text)) { lines.Add(field + ":"); lines.AddRange(DocxGenerator.HtmlToPlainText(text).Split('\n').Select(x => "  " + x)); }
                        }
        }
        return string.Join("\n", lines);
    }
}
