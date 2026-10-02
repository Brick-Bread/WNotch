using System.Security.Cryptography;

namespace Notch.Core.Updates;

/// <summary>Finds newer releases and downloads their installers, checking each download before it is trusted.</summary>
public sealed class Updater(HttpClient http, string? signingKey = UpdateSigning.PublicKey)
{
    private const int MaxSignatureLength = 1024;

    /// <summary>The latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    /// <exception cref="HttpRequestException">GitHub could not be reached.</exception>
    public async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.LatestReleaseUrl);

        // GitHub rejects API requests without a user agent.
        request.Headers.UserAgent.ParseAdd("Notch-Updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();

        ReleaseInfo? release = ReleaseFeed.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return release is not null && ReleaseFeed.IsNewer(release, current) ? release : null;
    }

    /// <summary>Downloads the installer into <paramref name="folder"/> and returns its path.</summary>
    /// <exception cref="InvalidDataException">The download does not match the size or digest GitHub lists for it.</exception>
    public async Task<string> DownloadAsync(ReleaseInfo release, string folder, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Notch-Setup-{release.Version}.exe");
        string partial = path + ".part";

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUrl))
            {
                request.Headers.UserAgent.ParseAdd("Notch-Updater");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();

                await using FileStream file = File.Create(partial);
                await response.Content.CopyToAsync(file, cancellation);
            }

            await VerifyAsync(release, partial, cancellation);
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    private async Task VerifyAsync(ReleaseInfo release, string path, CancellationToken cancellation)
    {
        long size = new FileInfo(path).Length;
        if (size == 0 || (release.InstallerSize > 0 && size != release.InstallerSize))
        {
            throw new InvalidDataException($"The installer is {size} bytes; the release lists {release.InstallerSize}.");
        }

        bool signed = UpdateSigning.IsConfigured(signingKey);
        if (signed)
        {
            await VerifySignatureAsync(release, path, cancellation);
        }
        else if (release.Sha256 is null)
        {
            // Nothing to check it against: an installer that cannot be verified is not run.
            throw new InvalidDataException("The release lists no digest for the installer and is not signed, so it cannot be verified.");
        }

        if (release.Sha256 is null)
        {
            return;
        }

        await using FileStream file = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The installer does not match the digest the release lists for it.");
        }
    }

    /// <summary>The installer must carry a valid signature from the maintainer's key.</summary>
    private async Task VerifySignatureAsync(ReleaseInfo release, string path, CancellationToken cancellation)
    {
        if (release.SignatureUrl is null)
        {
            throw new InvalidDataException("The installer is not signed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, release.SignatureUrl);
        request.Headers.UserAgent.ParseAdd("Notch-Updater");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSignatureLength)
        {
            throw new InvalidDataException("The installer's signature is not a signature.");
        }

        string signature = await response.Content.ReadAsStringAsync(cancellation);
        if (signature.Length > MaxSignatureLength || !UpdateSigning.Verify(path, signature, signingKey!))
        {
            throw new InvalidDataException("The installer's signature is not valid.");
        }
    }
}
