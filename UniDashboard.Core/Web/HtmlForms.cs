using System.Net;
using System.Text.RegularExpressions;

namespace UniDashboard.Core.Web;

public sealed record FormField(string Name, string Type, string Value)
{
    public bool IsButton => Type is "submit" or "button" or "image" or "reset";
}

public sealed record HtmlForm(string Action, string Method, IReadOnlyList<FormField> Fields)
{
    public bool Has(string name) => Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool HasPassword => Fields.Any(f => f.Type == "password");

    /// <summary>The fields a browser would send: every non-button field, plus the one button that was "clicked" (if any).</summary>
    public List<KeyValuePair<string, string>> Submission(FormField? clicked = null)
    {
        var list = Fields.Where(f => !f.IsButton).Select(f => KeyValuePair.Create(f.Name, f.Value)).ToList();
        if (clicked is not null) list.Add(KeyValuePair.Create(clicked.Name, clicked.Value));
        return list;
    }
}

/// <summary>
/// Minimal, dependency-free HTML scanner: just enough to follow the university's sign-in pages (forms with hidden fields, the SAML
/// auto-post form, links, meta refresh). It is not a general HTML parser and never executes anything.
/// </summary>
public static class HtmlForms
{
    static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Scripts = new(@"<(script|style)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:""[^""]*""|'[^']*'|[^>""'])*)>", RegexOptions.Compiled);
    static readonly Regex Attr = new(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", RegexOptions.Compiled);

    static string Clean(string html) => Scripts.Replace(Comments.Replace(html, ""), "");

    static Dictionary<string, string> Attrs(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr.Matches(raw))
        {
            var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : "";
            d[m.Groups[1].Value] = WebUtility.HtmlDecode(v);
        }
        return d;
    }

    static IEnumerable<(string Name, bool Closing, Dictionary<string, string> Attrs)> Tags(string html)
    {
        foreach (Match m in Tag.Matches(Clean(html)))
            yield return (m.Groups[2].Value.ToLowerInvariant(), m.Groups[1].Value == "/", Attrs(m.Groups[3].Value));
    }

    public static IReadOnlyList<HtmlForm> Parse(string html)
    {
        var forms = new List<HtmlForm>();
        string action = "", method = "GET";
        List<FormField>? fields = null;
        void Flush() { if (fields is not null) forms.Add(new(action, method, fields)); fields = null; }

        foreach (var (name, closing, a) in Tags(html))
        {
            if (name == "form")
            {
                Flush();
                if (closing) continue;
                action = a.GetValueOrDefault("action", "");
                method = a.GetValueOrDefault("method", "GET").ToUpperInvariant();
                fields = [];
            }
            else if (fields is not null && !closing && name is "input" or "button")
            {
                var n = a.GetValueOrDefault("name", "");
                if (n.Length == 0) continue;
                var type = a.GetValueOrDefault("type", name == "button" ? "submit" : "text").ToLowerInvariant();
                if (type is "checkbox" or "radio" && !a.ContainsKey("checked")) continue;
                fields.Add(new(n, type, a.GetValueOrDefault("value", "")));
            }
        }
        Flush();
        return forms;
    }

    /// <summary>First <c>&lt;a href&gt;</c> whose href contains <paramref name="hrefPart"/>.</summary>
    public static string? FindLink(string html, string hrefPart) =>
        Tags(html).Where(t => t.Name == "a" && !t.Closing && t.Attrs.TryGetValue("href", out var h) && h.Contains(hrefPart, StringComparison.OrdinalIgnoreCase))
                  .Select(t => t.Attrs["href"]).FirstOrDefault();

    /// <summary>True when any element carries one of the given CSS classes (used to spot sign-in error messages).</summary>
    public static bool HasAnyClass(string html, params string[] classes) =>
        Tags(html).Any(t => !t.Closing && t.Attrs.TryGetValue("class", out var c) &&
                            c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Intersect(classes, StringComparer.OrdinalIgnoreCase).Any());

    /// <summary><c>&lt;meta http-equiv="refresh" content="0; url=…"&gt;</c> target, if present.</summary>
    public static string? MetaRefreshTarget(string html)
    {
        foreach (var t in Tags(html))
        {
            if (t.Name != "meta" || t.Closing || !t.Attrs.TryGetValue("http-equiv", out var he) || !he.Equals("refresh", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(t.Attrs.GetValueOrDefault("content", ""), @"url\s*=\s*['""]?([^'""]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }
}
