using System.Net;
using System.Security.Cryptography;
using System.Text;
using Notch.Core.Updates;

namespace Notch.Core.Tests;

public class UpdateTests
{
    private const string Download = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe";

    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is an installer");

    private static string ReleaseJson(string tag = "v0.2.0", string url = Download, long? size = null, string? digest = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/notes.txt", "size": 3 },
            { "name": "Notch-Setup-0.2.0.exe", "browser_download_url": "{{url}}", "size": {{size ?? Installer.Length}}, "digest": {{(digest is null ? "null" : $"\"{digest}\"")}} }
          ]
        }
        """;

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void Tags_parse_to_versions(string tag, string expected)
    {
        Assert.True(ReleaseFeed.TryParseVersion(tag, out Version version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Nonsense_tags_are_rejected() =>
        Assert.False(ReleaseFeed.TryParseVersion("nightly", out _));

    [Fact]
    public void Release_picks_the_installer_asset()
    {
        ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(digest: "sha256:ABCDEF"))!;

        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal(Download, release.InstallerUrl);
        Assert.Equal(Installer.Length, release.InstallerSize);
        Assert.Equal("abcdef", release.Sha256);
    }

    [Theory]
    [InlineData("https://example.com/Notch-Setup-0.2.0.exe")]
    [InlineData("https://github.com/someone-else/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe")]
    [InlineData("http://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe")]
    public void Installers_from_anywhere_else_are_ignored(string url) =>
        Assert.Null(ReleaseFeed.Parse(ReleaseJson(url: url)));

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"tag_name\":\"v1.0.0\",\"assets\":[]}")]
    public void Unusable_releases_parse_to_null(string json) =>
        Assert.Null(ReleaseFeed.Parse(json));

    [Theory]
    [InlineData("0.1.0.0", true)]
    [InlineData("0.2.0.0", false)]
    [InlineData("0.2.0", false)]
    [InlineData("0.3.0", false)]
    public void Only_newer_releases_count(string current, bool expected) =>
        Assert.Equal(expected, ReleaseFeed.IsNewer(ReleaseFeed.Parse(ReleaseJson())!, Version.Parse(current)));

    [Fact]
    public async Task Check_returns_a_release_only_when_it_is_newer()
    {
        using var http = new HttpClient(new StubHandler(_ => ReleaseJson()));
        var updater = new Updater(http);

        Assert.NotNull(await updater.CheckAsync(new Version(0, 1, 0)));
        Assert.Null(await updater.CheckAsync(new Version(0, 2, 0)));
    }

    [Fact]
    public async Task Download_saves_a_verified_installer()
    {
        string folder = TempFolder();
        try
        {
            using var http = new HttpClient(new StubHandler(_ => Installer));
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(digest: Sha256(Installer)))!;

            string path = await new Updater(http).DownloadAsync(release, folder);

            Assert.Equal(Installer, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Download_rejects_a_tampered_installer()
    {
        string folder = TempFolder();
        try
        {
            using var http = new HttpClient(new StubHandler(_ => Installer));
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(digest: Sha256(Encoding.UTF8.GetBytes("something else entirely!!!!!"))))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http).DownloadAsync(release, folder));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Download_rejects_a_truncated_installer()
    {
        string folder = TempFolder();
        try
        {
            using var http = new HttpClient(new StubHandler(_ => Installer));
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(size: Installer.Length + 100))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http).DownloadAsync(release, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");

    private sealed class StubHandler(Func<string, object> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body = respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent((string)body),
            });
        }
    }
}
