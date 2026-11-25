using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace UniDashboard.Core.Docx;

/// <summary>Fills a .docx/.dotx template. Word splits placeholders across runs, so runs are merged per paragraph first.</summary>
public static partial class DocxGenerator
{
    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")] private static partial Regex Placeholder();

    public static Dictionary<string, string> BuildValues(Assignment a, AppSettings s, string moodleUrl)
    {
        var de = CultureInfo.GetCultureInfo("de-AT");
        var mat = s.Matrikelnummer.Trim();
        return new()
        {
            ["Title"] = a.Name,
            ["Course"] = a.CourseName,
            ["DueDate"] = a.DueAt?.ToLocalTime().ToString("dddd, d. MMMM yyyy, HH:mm", de) ?? "",
            ["Date"] = DateTime.Today.ToString("d. MMMM yyyy", de),
            ["Description"] = HtmlToPlainText(a.IntroHtml),
            ["MoodleUrl"] = moodleUrl,
            ["Author"] = s.StudentName.Trim(),
            ["Matrikel"] = mat.Length > 0 ? "Matrikelnummer " + mat : "",
        };
    }

    /// <summary>Returns the filled document as bytes. Nothing is cached or stored.</summary>
    public static byte[] Generate(string templatePath, IReadOnlyDictionary<string, string> values)
    {
        using var ms = new MemoryStream();
        using (var fs = File.OpenRead(templatePath)) fs.CopyTo(ms);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            // A .dotx must be turned into a document to be openable as .docx
            if (doc.DocumentType != WordprocessingDocumentType.Document)
                doc.ChangeDocumentType(WordprocessingDocumentType.Document);
            var main = doc.MainDocumentPart!;
            Fill(main.Document!.Body!, values);
            foreach (var h in main.HeaderParts) Fill(h.Header!, values);
            foreach (var f in main.FooterParts) Fill(f.Footer!, values);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    public static string SuggestFileName(Assignment a) =>
        Sanitize($"{a.CourseName} - {a.Name}") + ".docx";

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 120 ? s[..120].Trim() : s.Trim();
    }

    static void Fill(OpenXmlElement root, IReadOnlyDictionary<string, string> values)
    {
        foreach (var p in root.Descendants<Paragraph>().ToList())
        {
            var text = ParagraphText(p);
            if (!text.Contains("{{")) continue;
            MergeRuns(p);
            if (p.Elements<Run>().Any(r => r.Elements<TabChar>().Any()))
            {
                // Tab-separated paragraph (e.g. header "Name · Matrikel <tab> Date"): replace each segment in place.
                foreach (var r in p.Elements<Run>().Where(r => r.Elements<Text>().Any()))
                {
                    var seg = string.Concat(r.Elements<Text>().Select(t => t.Text));
                    SetRunText(r, CleanSeparators(Placeholder().Replace(seg, m => values.GetValueOrDefault(m.Groups[1].Value, m.Value))));
                }
                continue;
            }
            var replaced = Placeholder().Replace(ParagraphText(p), m => values.GetValueOrDefault(m.Groups[1].Value, m.Value));
            replaced = CleanSeparators(replaced);

            var run = p.Elements<Run>().FirstOrDefault();
            if (run is null) continue;
            var lines = replaced.Split('\n');
            if (lines.Length > 1 && ParagraphText(p).Trim() == "{{Description}}")
            {
                // Multi-paragraph value: clone the paragraph once per line.
                OpenXmlElement last = p;
                for (var i = 1; i < lines.Length; i++)
                {
                    var clone = (Paragraph)p.CloneNode(true);
                    SetRunText(clone.Elements<Run>().First(), lines[i]);
                    last.InsertAfterSelf(clone);
                    last = clone;
                }
            }
            SetRunText(run, lines[0]);
        }
    }

    static string ParagraphText(Paragraph p) => string.Concat(p.Descendants<Text>().Select(t => t.Text));

    /// <summary>Collapse all runs of a paragraph into the first, keeping its formatting (tabs preserved as separators).</summary>
    static void MergeRuns(Paragraph p)
    {
        var runs = p.Elements<Run>().ToList();
        if (runs.Count <= 1) return;
        var hasTab = runs.Any(r => r.Elements<TabChar>().Any());
        if (hasTab)
        {
            // Keep tab-separated segments: merge consecutive text runs between tabs.
            var segments = new List<List<Run>> { new() };
            foreach (var r in runs)
            {
                if (r.Elements<TabChar>().Any()) segments.Add([]);
                else segments[^1].Add(r);
            }
            foreach (var seg in segments.Where(sg => sg.Count > 1))
                MergeInto(seg);
            return;
        }
        MergeInto(runs);
    }

    static void MergeInto(List<Run> runs)
    {
        var first = runs[0];
        var text = string.Concat(runs.SelectMany(r => r.Elements<Text>()).Select(t => t.Text));
        foreach (var r in runs.Skip(1)) r.Remove();
        SetRunText(first, text);
    }

    static void SetRunText(Run run, string text)
    {
        foreach (var t in run.Elements<Text>().ToList()) t.Remove();
        foreach (var b in run.Elements<Break>().ToList()) b.Remove();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>Removes dangling " · " left over when an optional value is empty.</summary>
    static string CleanSeparators(string s) => Regex.Replace(s, @"^\s*·\s*|\s*·\s*$", "");

    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = Regex.Replace(html, @"<\s*br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*(p|div|h[1-6]|ul|ol|tr)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ').Replace("\r", "");
        var lines = s.Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}
