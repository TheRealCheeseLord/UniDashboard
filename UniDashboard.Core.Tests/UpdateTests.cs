using System.Net;
using System.Security.Cryptography;
using System.Text;
using UniDashboard.Core;
using UniDashboard.Core.Update;
using Xunit;

namespace UniDashboard.Core.Tests;

public class UpdateTests
{
    static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is Setup.exe");
    static readonly string InstallerHash = Convert.ToHexString(SHA256.HashData(Installer));

    /// <summary>Fake GitHub: release JSON at the api url, assets under /dl/.</summary>
    sealed class Gh(string releaseJson, byte[]? setup = null, string? sidecar = null, HttpStatusCode release = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            if (url.EndsWith("/latest") || url.Contains("/releases?per_page=")) return Task.FromResult(new HttpResponseMessage(release) { Content = new StringContent(releaseJson) });
            if (url.EndsWith(".sha256") && sidecar is not null) return Task.FromResult(new HttpResponseMessage { Content = new StringContent(sidecar) });
            if (url.EndsWith(".exe") && setup is not null) return Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(setup) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static string Release(string tag, bool prerelease = false, bool withSidecar = true, bool draft = false, string? digest = null) => $$"""
        {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"body":"Notes","html_url":"https://example/rel",
         "assets":[{"name":"UniDashboard-Setup-{{tag.TrimStart('v')}}.exe","browser_download_url":"https://example/dl/UniDashboard-Setup-{{tag.TrimStart('v')}}.exe"{{(digest is null ? "" : $",\"digest\":\"sha256:{digest}\"")}}}
                   {{(withSidecar ? $$""",{"name":"UniDashboard-Setup-{{tag.TrimStart('v')}}.exe.sha256","browser_download_url":"https://example/dl/UniDashboard-Setup-{{tag.TrimStart('v')}}.exe.sha256"}""" : "")}}]}
        """;

    const string Latest = "https://example/repos/x/releases/latest";

    static UpdateService Svc(Gh h, string running = "0.1.0") => new(new HttpClient(h), () => new Version(running), Latest);

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("0.4.0-rc1", "0.4.0")]
    [InlineData("2.0", "2.0.0")]
    [InlineData("garbage", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void Versions_are_parsed_leniently(string? input, string expected) =>
        Assert.Equal(new Version(expected), AppVersion.Parse(input));

    [Fact]
    public async Task Newer_release_is_reported_with_its_verified_hash()
    {
        var u = await Svc(new Gh(Release("v0.2.0"), Installer, InstallerHash + "  UniDashboard-Setup-0.2.0.exe\n")).CheckAsync();
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 2, 0), u!.Version);
        Assert.Equal(InstallerHash, u.Sha256);
        Assert.Equal("Notes", u.Notes);
        Assert.EndsWith("UniDashboard-Setup-0.2.0.exe", u.DownloadUrl);
    }

    [Theory]
    [InlineData("v0.1.0")]   // same
    [InlineData("v0.0.9")]   // older
    public async Task Same_or_older_release_is_not_an_update(string tag) =>
        Assert.Null(await Svc(new Gh(Release(tag), Installer, InstallerHash)).CheckAsync());

    [Fact]
    public async Task Prereleases_and_drafts_are_ignored()
    {
        Assert.Null(await Svc(new Gh(Release("v0.3.0", prerelease: true), Installer, InstallerHash)).CheckAsync());
        Assert.Null(await Svc(new Gh(Release("v0.3.0", draft: true), Installer, InstallerHash)).CheckAsync());
    }

    [Fact]
    public async Task Release_without_a_published_hash_is_ignored() =>
        Assert.Null(await Svc(new Gh(Release("v0.2.0", withSidecar: false), Installer)).CheckAsync());

    [Fact]
    public async Task Githubs_asset_digest_is_used_when_there_is_no_sidecar()
    {
        var u = await Svc(new Gh(Release("v0.2.0", withSidecar: false, digest: InstallerHash.ToLowerInvariant()), Installer)).CheckAsync();
        Assert.Equal(InstallerHash, u!.Sha256);
    }

    [Fact]
    public async Task Release_list_is_newest_first_and_includes_prereleases_but_skips_unusable_ones()
    {
        var json = "[" + string.Join(",", Release("v0.1.0"), Release("v0.3.0", prerelease: true), Release("v0.2.0"), Release("v0.9.0", draft: true), Release("v0.8.0", withSidecar: false)) + "]";
        var list = await Svc(new Gh(json, Installer, InstallerHash), "0.2.0").ListReleasesAsync();
        Assert.Equal(["0.3.0", "0.2.0", "0.1.0"], list.Select(r => r.Version.ToString(3)));
        Assert.True(list[0].Prerelease);
    }

    [Fact]
    public async Task Configured_repo_decides_which_url_is_asked()
    {
        var seen = new List<string>();
        var svc = new UpdateService(new HttpClient(new Recording(seen)), () => new Version(0, 1, 0), repo: () => "someone/fork");
        await svc.CheckAsync();
        await svc.ListReleasesAsync();
        Assert.Equal(["https://api.github.com/repos/someone/fork/releases/latest", "https://api.github.com/repos/someone/fork/releases?per_page=30"], seen);
    }

    sealed class Recording(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            seen.Add(r.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Theory]
    [InlineData("owner/repo", true)]
    [InlineData("a-b.c_d/e", true)]
    [InlineData("owner", false)]
    [InlineData("a/b/c", false)]
    [InlineData("a b/c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Repo_names_are_validated(string? repo, bool ok) => Assert.Equal(ok, UpdateService.IsValidRepo(repo));

    [Fact]
    public async Task Release_without_an_installer_asset_is_ignored() =>
        Assert.Null(await Svc(new Gh("""{"tag_name":"v0.2.0","assets":[{"name":"notes.txt","browser_download_url":"https://example/dl/notes.txt"}]}""")).CheckAsync());

    [Fact]
    public async Task Network_and_api_errors_mean_no_update_instead_of_a_crash()
    {
        Assert.Null(await Svc(new Gh("{}", release: HttpStatusCode.Forbidden)).CheckAsync());          // rate limited
        Assert.Null(await Svc(new Gh("not json")).CheckAsync());
        Assert.Null(await new UpdateService(new HttpClient(new Throwing()), () => new Version(0, 1, 0), Latest).CheckAsync());
    }

    sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Download_is_verified_and_returns_the_file()
    {
        var svc = Svc(new Gh(Release("v0.2.0"), Installer, InstallerHash));
        var u = await svc.CheckAsync();
        var path = await svc.DownloadAsync(u!);
        try { Assert.Equal(Installer, await File.ReadAllBytesAsync(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Tampered_download_is_rejected_and_deleted()
    {
        var svc = Svc(new Gh(Release("v0.2.0"), Encoding.UTF8.GetBytes("evil"), InstallerHash));
        var u = await svc.CheckAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.DownloadAsync(u!));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "UniDashboard-update", "UniDashboard-Setup-0.2.0.exe")));
    }

    [Fact]
    public async Task A_local_file_url_works_for_offline_testing()
    {
        var dir = Directory.CreateTempSubdirectory("unidash-upd").FullName;
        try
        {
            var setup = Path.Combine(dir, "UniDashboard-Setup-0.2.0.exe");
            await File.WriteAllBytesAsync(setup, Installer);
            var rel = Path.Combine(dir, "latest.json");
            var fileUrl = new Uri(setup).AbsoluteUri;
            await File.WriteAllTextAsync(rel, $$"""{"tag_name":"v0.2.0","assets":[{"name":"UniDashboard-Setup-0.2.0.exe","browser_download_url":"{{fileUrl}}","digest":"sha256:{{InstallerHash}}"}]}""");
            var svc = new UpdateService(new HttpClient(new Throwing()), () => new Version(0, 1, 0), new Uri(rel).AbsoluteUri);
            var u = await svc.CheckAsync();
            Assert.NotNull(u);
            Assert.Equal(Installer, await File.ReadAllBytesAsync(await svc.DownloadAsync(u!)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
