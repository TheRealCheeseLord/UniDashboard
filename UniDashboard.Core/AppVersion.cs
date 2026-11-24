using System.Reflection;

namespace UniDashboard.Core;

/// <summary>The running version (from Directory.Build.props / the release tag).</summary>
public static class AppVersion
{
    public static Version Current { get; } = Parse(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Text => Current.ToString(3);

    /// <summary>Parses "v1.2.3", "1.2.3+abc" or "1.2.3-rc1" into 1.2.3 (null / unparsable → 0.0.0).</summary>
    public static Version Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0, 0);
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['+', '-']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : new Version(0, 0, 0);
    }

    static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
