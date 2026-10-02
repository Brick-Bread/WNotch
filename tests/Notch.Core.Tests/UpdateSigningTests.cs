using System.Net;
using System.Security.Cryptography;
using System.Text;
using Notch.Core.Updates;

namespace Notch.Core.Tests;

public class UpdateSigningTests
{
    private const string InstallerUrl = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe";
    private const string SignatureUrl = InstallerUrl + ".sig";

    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is an installer");

    private static string ReleaseJson(bool withSignature = true, string? digest = null) => $$"""
        {
          "tag_name": "v0.2.0",
          "assets": [
            { "name": "Notch-Setup-0.2.0.exe", "browser_download_url": "{{InstallerUrl}}", "size": {{Installer.Length}}, "digest": {{(digest is null ? "null" : $"\"{digest}\"")}} }
            {{(withSignature ? $$""", { "name": "Notch-Setup-0.2.0.exe.sig", "browser_download_url": "{{SignatureUrl}}", "size": 88 }""" : "")}}
          ]
        }
        """;

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string TempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"notch-sign-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static HttpClient Http(string signatureBody, byte[]? installer = null) =>
        new(new StubHandler(url => url == SignatureUrl ? signatureBody : installer ?? Installer));

    [Fact]
    public void A_signature_verifies_only_for_the_file_and_key_it_was_made_with()
    {
        (string publicKey, string privateKey) = UpdateSigning.NewKeyPair();
        (string otherPublic, _) = UpdateSigning.NewKeyPair();
        string file = TempFile(Installer);
        string changed = TempFile([.. Installer, 0]);
        try
        {
            string signature = UpdateSigning.Sign(file, privateKey);

            Assert.True(UpdateSigning.Verify(file, signature, publicKey));
            Assert.False(UpdateSigning.Verify(changed, signature, publicKey));
            Assert.False(UpdateSigning.Verify(file, signature, otherPublic));
        }
        finally
        {
            File.Delete(file);
            File.Delete(changed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Malformed_signatures_are_simply_invalid(string? signature)
    {
        (string publicKey, _) = UpdateSigning.NewKeyPair();
        string file = TempFile(Installer);
        try
        {
            Assert.False(UpdateSigning.Verify(file, signature, publicKey));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_broken_public_key_never_verifies()
    {
        string file = TempFile(Installer);
        try
        {
            Assert.False(UpdateSigning.Verify(file, Convert.ToBase64String(new byte[64]), "nonsense"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_signature_asset_is_found_beside_the_installer()
    {
        Assert.Equal(SignatureUrl, ReleaseFeed.Parse(ReleaseJson())!.SignatureUrl);
        Assert.Null(ReleaseFeed.Parse(ReleaseJson(withSignature: false))!.SignatureUrl);
    }

    [Fact]
    public void A_signature_hosted_elsewhere_is_ignored()
    {
        string json = ReleaseJson().Replace(SignatureUrl, "https://example.com/Notch-Setup-0.2.0.exe.sig");
        Assert.Null(ReleaseFeed.Parse(json)!.SignatureUrl);
    }

    [Fact]
    public async Task A_signed_installer_is_downloaded()
    {
        (string publicKey, string privateKey) = UpdateSigning.NewKeyPair();
        string signature = SignFor(Installer, privateKey);
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            using HttpClient http = Http(signature);
            string path = await new Updater(http, publicKey).DownloadAsync(ReleaseFeed.Parse(ReleaseJson(digest: Digest(Installer)))!, folder);
            Assert.Equal(Installer, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task With_a_key_an_unsigned_release_is_refused_even_with_a_matching_digest()
    {
        (string publicKey, _) = UpdateSigning.NewKeyPair();
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            using HttpClient http = Http("");
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(withSignature: false, digest: Digest(Installer)))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http, publicKey).DownloadAsync(release, folder));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused()
    {
        (string publicKey, _) = UpdateSigning.NewKeyPair();
        (_, string attackerKey) = UpdateSigning.NewKeyPair();
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            using HttpClient http = Http(SignFor(Installer, attackerKey));
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(digest: Digest(Installer)))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http, publicKey).DownloadAsync(release, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_replaced_installer_fails_even_when_github_lists_its_digest()
    {
        // Someone who can edit the release can also change the digest GitHub shows; they cannot sign.
        (string publicKey, string privateKey) = UpdateSigning.NewKeyPair();
        byte[] evil = Encoding.UTF8.GetBytes("pretend this is malware....");
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            using HttpClient http = Http(SignFor(Installer, privateKey), installer: evil);
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(digest: Digest(evil)))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http, publicKey).DownloadAsync(release, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_key_a_release_with_no_digest_is_no_longer_trusted()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            using HttpClient http = Http("");
            ReleaseInfo release = ReleaseFeed.Parse(ReleaseJson(withSignature: false))!;

            await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(http, signingKey: "").DownloadAsync(release, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_embedded_key_is_empty_until_the_maintainer_sets_one()
    {
        // This documents the state of the repository: flip it when installer/new-update-key.ps1 has been run.
        Assert.Equal(UpdateSigning.PublicKey.Length > 0, UpdateSigning.IsConfigured());
    }

    private static string SignFor(byte[] content, string privateKey)
    {
        string file = TempFile(content);
        try
        {
            return UpdateSigning.Sign(file, privateKey);
        }
        finally
        {
            File.Delete(file);
        }
    }

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
