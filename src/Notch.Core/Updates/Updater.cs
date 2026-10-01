using System.Security.Cryptography;

namespace Notch.Core.Updates;

/// <summary>Finds newer releases and downloads their installers, checking each download before it is trusted.</summary>
public sealed class Updater(HttpClient http)
{
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

    private static async Task VerifyAsync(ReleaseInfo release, string path, CancellationToken cancellation)
    {
        long size = new FileInfo(path).Length;
        if (size == 0 || (release.InstallerSize > 0 && size != release.InstallerSize))
        {
            throw new InvalidDataException($"The installer is {size} bytes; the release lists {release.InstallerSize}.");
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
}
