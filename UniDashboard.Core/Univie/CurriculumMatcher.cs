using System.Globalization;
using System.Text.RegularExpressions;
using UniDashboard.Core.Ufind;
using UniDashboard.Core.Uspace;

namespace UniDashboard.Core.Univie;

/// <summary>A curriculum as named by u:find, e.g. "Diplom Katholische Fachtheologie (011 [5] - Version 2026)".</summary>
public record CurriculumCandidate(string RootPath, string Name, int? VersionYear, bool Discontinued, List<CurriculumCodeGroup> Groups);

/// <summary>One code group inside the parentheses, e.g. "193 055" or "011 [5]": the study codes and the optional version mark.</summary>
public record CurriculumCodeGroup(HashSet<string> Codes, string? Zusatz);

/// <summary>
/// Pure logic: which u:find curriculum is "my" study? u:space says which studies the student has (codes + version mark),
/// u:find says which curricula an enrolled course is listed in. A course appears in many curricula (other programmes, old versions),
/// so the study codes pick the programme and the version mark / start year pick the version.
/// </summary>
public static class CurriculumMatcher
{
    static readonly Regex Paren = new(@"\((?<codes>[^()]*?)\s*-\s*Version\s*(?<year>\d{4})[^()]*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Group = new(@"(?<c>\d{3}(?:\s+\d{3})*)(?:\s*\[(?<z>[^\]]+)\])?", RegexOptions.Compiled);

    public static CurriculumCandidate? ParseCategory(string path, string name)
    {
        var parts = path.Split('|');
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(name)) return null;
        var m = Paren.Matches(name).LastOrDefault();
        if (m is null) return null;
        var groups = new List<CurriculumCodeGroup>();
        foreach (Match g in Group.Matches(m.Groups["codes"].Value))
            groups.Add(new(g.Groups["c"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(),
                g.Groups["z"].Success ? g.Groups["z"].Value.Trim() : null));
        if (groups.Count == 0) return null;
        return new(parts[2], name, int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture),
            name.Contains("auslaufend", StringComparison.OrdinalIgnoreCase) || name.Contains("discontinued", StringComparison.OrdinalIgnoreCase), groups);
    }

    /// <summary>The study's codes must all be present in one code group of the curriculum.</summary>
    public static bool Matches(StudyDto study, CurriculumCandidate c) =>
        study.Kennzahlen.Count > 0 && c.Groups.Any(g => study.Kennzahlen.All(g.Codes.Contains));

    /// <summary>
    /// For each study, the single best curriculum among the chapters of the enrolled courses (null: none found).
    /// Preference: version mark equal to the study's, then not discontinued, then newest version not newer than the study start.
    /// </summary>
    public static List<(StudyDto Study, CurriculumCandidate Curriculum)> Select(IEnumerable<StudyDto> studies, IEnumerable<UfindChapter> chapters)
    {
        var candidates = chapters.Select(ch =>
            {
                var parts = ch.Path.Split('|');
                // The category path is the first three ids of the chapter path.
                return parts.Length >= 3 ? ParseCategory(string.Join('|', parts.Take(3)), ch.Category) : null;
            })
            .OfType<CurriculumCandidate>().GroupBy(c => c.RootPath).Select(g => g.First()).ToList();

        var result = new List<(StudyDto, CurriculumCandidate)>();
        foreach (var study in studies)
        {
            var hits = candidates.Where(c => Matches(study, c)).ToList();
            if (hits.Count == 0) continue;
            if (study.Zusatz.Count > 0)
            {
                var exact = hits.Where(c => c.Groups.Any(g => g.Zusatz is not null && study.Zusatz.Contains(g.Zusatz) && study.Kennzahlen.All(g.Codes.Contains))).ToList();
                if (exact.Count > 0) hits = exact;
            }
            var best = hits
                .OrderBy(c => c.Discontinued)
                .ThenByDescending(c => study.BeginYear is { } y && c.VersionYear is { } v ? (v <= y ? 1 : 0) : 0)
                .ThenByDescending(c => c.VersionYear ?? 0)
                .First();
            result.Add((study, best));
        }
        return result;
    }
}
