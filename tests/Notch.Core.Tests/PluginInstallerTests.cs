using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginSourceTests
{
    [Theory]
    [InlineData("acme/build-status", "acme", "build-status")]
    [InlineData("  acme/build-status  ", "acme", "build-status")]
    [InlineData("https://github.com/acme/build-status", "acme", "build-status")]
    [InlineData("https://github.com/acme/build-status.git", "acme", "build-status")]
    [InlineData("github.com/acme/build.status/releases/tag/v1.0.0", "acme", "build.status")]
    [InlineData("https://www.github.com/acme/build-status?tab=readme", "acme", "build-status")]
    public void Reads_names_and_links(string text, string owner, string repository) =>
        Assert.Equal(new PluginSource(owner, repository), PluginSource.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("just-a-name")]
    [InlineData("https://example.com/acme/build-status")]
    [InlineData("acme/..")]
    [InlineData("-acme/repo")]
    [InlineData("acme/has spaces")]
    public void Rejects_anything_else(string text) =>
        Assert.Null(PluginSource.Parse(text));
}

public sealed class PluginInstallerTests : IDisposable
{
    private const string Download = "https://github.com/acme/hello/releases/download/v1.0.0/hello.zip";

    private readonly string _plugins = Path.Combine(Path.GetTempPath(), "notch-install-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_plugins))
        {
            Directory.Delete(_plugins, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello-1.0.0/")]
    public async Task Installs_the_zip_of_the_latest_release(string folderInZip)
    {
        byte[] zip = Zip(folderInZip, version: "1.0.0");
        PluginInstaller installer = CreateInstaller(zip, digest: Sha256(zip));

        PluginInstallResult result = await installer.InstallAsync("acme/hello");

        Assert.Equal("acme.hello", result.Manifest.Id);
        Assert.Equal("v1.0.0", result.Tag);
        Assert.False(result.Pending);
        Assert.True(File.Exists(Path.Combine(_plugins, "acme.hello", PluginManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(_plugins, "acme.hello", "Hello.dll")));
        Assert.True(File.Exists(Path.Combine(_plugins, "acme.hello", "lib", "Dependency.dll")));
        Assert.Equal(["acme.hello"], Directory.GetFileSystemEntries(_plugins).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Replaces_an_installed_plugin_that_is_not_loaded_whatever_its_folder_is_called()
    {
        string existing = Path.Combine(_plugins, "copied-by-hand");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, PluginManifest.FileName), ManifestJson("0.9.0"));
        File.WriteAllText(Path.Combine(existing, "stale.txt"), "from the old version");

        PluginInstallResult result = await CreateInstaller(Zip("", version: "1.0.0")).InstallAsync("acme/hello");

        Assert.False(result.Pending);
        Assert.Equal(["copied-by-hand"], Directory.GetFileSystemEntries(_plugins).Select(Path.GetFileName));
        Assert.Equal("1.0.0", PluginManifest.Parse(File.ReadAllText(Path.Combine(existing, PluginManifest.FileName))).Version);
        Assert.False(File.Exists(Path.Combine(existing, "stale.txt")));
    }

    [Fact]
    public async Task A_loaded_plugin_is_replaced_at_the_next_start()
    {
        string existing = Path.Combine(_plugins, "acme.hello");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, PluginManifest.FileName), ManifestJson("0.9.0"));
        PluginInstaller installer = CreateInstaller(Zip("", version: "1.0.0"), isLoaded: id => id == "acme.hello");

        PluginInstallResult result = await installer.InstallAsync("acme/hello");

        Assert.True(result.Pending);
        Assert.Equal("0.9.0", PluginManifest.Parse(File.ReadAllText(Path.Combine(existing, PluginManifest.FileName))).Version);

        PluginInstaller.ApplyPendingUpdates(_plugins);

        Assert.Equal(["acme.hello"], Directory.GetFileSystemEntries(_plugins).Select(Path.GetFileName));
        Assert.Equal("1.0.0", PluginManifest.Parse(File.ReadAllText(Path.Combine(existing, PluginManifest.FileName))).Version);
    }

    [Fact]
    public async Task Rejects_a_download_that_does_not_match_its_digest()
    {
        PluginInstaller installer = CreateInstaller(Zip("", "1.0.0"), digest: Sha256(Encoding.UTF8.GetBytes("something else")));

        var e = await Assert.ThrowsAsync<PluginLoadException>(() => installer.InstallAsync("acme/hello"));

        Assert.Contains("digest", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_plugins));
    }

    [Fact]
    public async Task Rejects_entries_that_point_outside_the_plugin_folder()
    {
        byte[] zip = Zip("", "1.0.0", extraEntry: "../../escaped.txt");

        var e = await Assert.ThrowsAsync<PluginLoadException>(() => CreateInstaller(zip).InstallAsync("acme/hello"));

        Assert.Contains("outside", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_plugins));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_plugins)!, "escaped.txt")));
    }

    [Fact]
    public async Task Rejects_a_zip_that_is_not_a_plugin()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "readme.txt", "no manifest here");
        }

        var e = await Assert.ThrowsAsync<PluginLoadException>(() => CreateInstaller(stream.ToArray()).InstallAsync("acme/hello"));

        Assert.Contains("not a Notch plugin", e.Message);
    }

    [Fact]
    public async Task Explains_a_release_without_a_zip()
    {
        using var http = new HttpClient(new StubHandler(_ => """{ "tag_name": "v1.0.0", "assets": [] }"""));

        var e = await Assert.ThrowsAsync<PluginLoadException>(() => new PluginInstaller(http, _plugins).InstallAsync("acme/hello"));

        Assert.Contains("no .zip", e.Message);
    }

    [Fact]
    public async Task Explains_a_repository_that_does_not_exist()
    {
        using var http = new HttpClient(new StubHandler(_ => HttpStatusCode.NotFound));

        var e = await Assert.ThrowsAsync<PluginLoadException>(() => new PluginInstaller(http, _plugins).InstallAsync("acme/hello"));

        Assert.Contains("was not found", e.Message);
    }

    [Fact]
    public async Task Explains_input_that_is_not_a_repository()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request expected.")));

        await Assert.ThrowsAsync<PluginLoadException>(() => new PluginInstaller(http, _plugins).InstallAsync("hello"));
    }

    private static string ManifestJson(string version) =>
        $$"""{ "id": "acme.hello", "name": "Hello", "version": "{{version}}", "assembly": "Hello.dll", "apiVersion": 1 }""";

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static byte[] Zip(string folder, string version, string? extraEntry = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, folder + PluginManifest.FileName, ManifestJson(version));
            Add(zip, folder + "Hello.dll", "pretend this is an assembly");
            Add(zip, folder + "lib/Dependency.dll", "and this a dependency");
            if (extraEntry is not null)
            {
                Add(zip, folder + extraEntry, "should never be written");
            }
        }

        return stream.ToArray();
    }

    /// <summary>An installer whose GitHub offers one release, v1.0.0, with <paramref name="zip"/> attached.</summary>
    private PluginInstaller CreateInstaller(byte[] zip, string? digest = null, Func<string, bool>? isLoaded = null)
    {
        string release = $$"""
            {
              "tag_name": "v1.0.0",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://github.com/acme/hello/releases/download/v1.0.0/notes.txt", "size": 3 },
                { "name": "hello.zip", "browser_download_url": "{{Download}}", "size": {{zip.Length}}, "digest": {{(digest is null ? "null" : $"\"{digest}\"")}} }
              ]
            }
            """;

        var http = new HttpClient(new StubHandler(url => url switch
        {
            "https://api.github.com/repos/acme/hello/releases/latest" => release,
            Download => zip,
            _ => HttpStatusCode.NotFound,
        }));
        return new PluginInstaller(http, _plugins, isLoaded);
    }

    private sealed class StubHandler(Func<string, object> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.ToString()) switch
            {
                byte[] bytes => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                string text => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) },
                HttpStatusCode status => new HttpResponseMessage(status),
                _ => throw new InvalidOperationException(),
            });
    }
}
