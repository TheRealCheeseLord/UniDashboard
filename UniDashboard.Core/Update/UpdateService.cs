using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace UniDashboard.Core.Update;

/// <summary>A newer release found on GitHub. <paramref name="Sha256"/> is always present (releases without one are ignored).</summary>
public record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, string Sha256, string PageUrl);

/// <summary>
/// Looks for a newer GitHub release, downloads its installer and verifies the SHA-256 before it is ever run.
/// Never throws for network problems: <see cref="CheckAsync"/> just reports "no update".
/// For tests, UNIDASHBOARD_UPDATE_URL may point at a release JSON (http(s):// or file://) instead of GitHub.
/// </summary>
public class UpdateService(HttpClient http, Func<Version>? current = null, string? releaseUrl = null, Action<string>? log = null)
{
    public const string DefaultReleaseUrl = "https://api.github.com/repos/TheRealCheeseLord/UniDashboard/releases/latest";
    const string InstallerPrefix = "UniDashboard-Setup-";

    readonly Version _current = (current ?? (() => AppVersion.Current))();
    readonly string _url = releaseUrl ?? (Environment.GetEnvironmentVariable("UNIDASHBOARD_UPDATE_URL") is { Length: > 0 } o ? o : DefaultReleaseUrl);

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetTextAsync(_url, ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (Bool(r, "draft") || Bool(r, "prerelease")) return null;

            var tag = Str(r, "tag_name") ?? "";
            var version = AppVersion.Parse(tag);
            if (version <= _current || version == new Version(0, 0, 0)) return null;

            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var list = assets.EnumerateArray().Select(a => (Name: Str(a, "name") ?? "", Url: Str(a, "browser_download_url") ?? "", Digest: Str(a, "digest"))).ToList();
            var setup = list.FirstOrDefault(a => a.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(setup.Url)) return null;

            var hash = await ReadHashAsync(list, setup, ct);
            if (hash is null) { log?.Invoke($"update {tag}: no SHA-256 published, ignoring"); return null; }

            return new UpdateInfo(version, tag, Str(r, "body") ?? "", setup.Url, hash, Str(r, "html_url") ?? "https://github.com/TheRealCheeseLord/UniDashboard/releases");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or TaskCanceledException or InvalidOperationException)
        {
            log?.Invoke("update check failed: " + e.Message);
            return null;
        }
    }

    /// <summary>Downloads the installer to a temp folder and verifies its SHA-256; deletes it and throws on mismatch.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "UniDashboard-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(new Uri(info.DownloadUrl).LocalPath));
        await DownloadToAsync(info.DownloadUrl, path, ct);

        string actual;
        await using (var fs = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch (IOException) { }
            throw new InvalidDataException("The downloaded installer does not match its published SHA-256; it was discarded.");
        }
        return path;
    }

    /// <summary>Runs the installer silently; it closes the app, upgrades in place and starts the new version.</summary>
    public static void StartInstaller(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /CLOSEAPPLICATIONS /LAUNCH") { UseShellExecute = true });

    // ---------- helpers

    async Task<string?> ReadHashAsync(List<(string Name, string Url, string? Digest)> assets, (string Name, string Url, string? Digest) setup, CancellationToken ct)
    {
        var sidecar = assets.FirstOrDefault(a => a.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (sidecar.Url?.Length > 0)
        {
            var first = (await GetTextAsync(sidecar.Url, ct)).Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (IsSha256(first)) return first!.ToUpperInvariant();
        }
        var digest = setup.Digest?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        return IsSha256(digest) ? digest!.ToUpperInvariant() : null;
    }

    static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile) return await File.ReadAllTextAsync(u.LocalPath, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("UniDashboard-updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    async Task DownloadToAsync(string url, string path, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile) { File.Copy(u.LocalPath, path, overwrite: true); return; }
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("UniDashboard-updater");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}
