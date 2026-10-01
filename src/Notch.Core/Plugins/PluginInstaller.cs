using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Notch.Core.Plugins;

/// <summary>A GitHub repository that publishes a plugin as a .zip attached to its releases.</summary>
public sealed partial record PluginSource(string Owner, string Repository)
{
    public string LatestReleaseUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>Reads "owner/repo" or a github.com link to the repository or anything inside it.</summary>
    public static PluginSource? Parse(string text)
    {
        Match match = Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        string repository = match.Groups["repo"].Value;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }

        return repository.Trim('.').Length == 0 ? null : new PluginSource(match.Groups["owner"].Value, repository);
    }

    public override string ToString() => $"{Owner}/{Repository}";

    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.)?github\.com/)?(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})(?:[/?#].*)?$")]
    private static partial Regex Pattern();
}

/// <param name="Pending">
/// True when the plugin was running, so its files could not be replaced: the new version is
/// set aside and takes over the next time Notch starts.
/// </param>
public sealed record PluginInstallResult(PluginManifest Manifest, string Tag, bool Pending);

/// <summary>
/// Installs plugins from GitHub: downloads the .zip attached to a repository's latest release,
/// checks it, and unpacks it into the plugins folder. Installing does not run or enable anything.
/// </summary>
/// <param name="isLoaded">
/// Whether the plugin with this id has had its assembly loaded in this run
/// (<see cref="PluginManager.IsLoaded"/>). Such a plugin is never replaced on the spot.
/// </param>
public sealed class PluginInstaller(HttpClient http, string pluginsDirectory, Func<string, bool>? isLoaded = null)
{
    private const long MaxDownloadBytes = 64L * 1024 * 1024;
    private const long MaxUnpackedBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 2000;

    // Work folders inside the plugins folder; the dot keeps PluginManager from listing them.
    private const string StagingPrefix = ".installing-";
    private const string UpdatePrefix = ".update-";
    private const string OldPrefix = ".old-";

    /// <summary>
    /// Puts updates in place that could not be installed while their plugin was running, and
    /// clears leftovers of interrupted installs. Call before any plugin is loaded.
    /// </summary>
    public static void ApplyPendingUpdates(string pluginsDirectory)
    {
        try
        {
            if (!Directory.Exists(pluginsDirectory))
            {
                return;
            }

            foreach (string update in Directory.GetDirectories(pluginsDirectory, UpdatePrefix + "*"))
            {
                string target = Path.Combine(pluginsDirectory, Path.GetFileName(update)[UpdatePrefix.Length..]);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(update, target);
            }

            foreach (string leftover in Directory.GetDirectories(pluginsDirectory, ".*")
                .Where(d => Path.GetFileName(d) is { } name
                    && (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || name.StartsWith(OldPrefix, StringComparison.Ordinal))))
            {
                Directory.Delete(leftover, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Whatever could not be moved stays set aside and is tried again at the next start.
        }
    }

    /// <param name="source">"owner/repo" or a link to the repository on github.com.</param>
    /// <exception cref="PluginLoadException">Nothing was installed; the message, meant for the user, says why.</exception>
    public async Task<PluginInstallResult> InstallAsync(string source, CancellationToken cancellation = default)
    {
        PluginSource repository = PluginSource.Parse(source)
            ?? throw new PluginLoadException("Enter a GitHub repository as owner/name, or paste its link.");

        string staging = Path.Combine(pluginsDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        string archive = staging + ".zip";
        try
        {
            Package package = await FindPackageAsync(repository, cancellation);
            Directory.CreateDirectory(pluginsDirectory);
            await DownloadAsync(package, archive, cancellation);

            PluginManifest manifest = Unpack(archive, staging);
            PluginOrigin.Write(staging, repository, package.Tag);
            bool pending = Place(staging, manifest);
            return new PluginInstallResult(manifest, package.Tag, pending);
        }
        catch (HttpRequestException e)
        {
            throw new PluginLoadException(e.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{repository} was not found on GitHub, or has not published a release.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub is not accepting more requests right now. Try again in a few minutes.",
                _ => "GitHub could not be reached: " + e.Message,
            }, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            throw new PluginLoadException("The plugin could not be installed: " + e.Message, e);
        }
        finally
        {
            TryDelete(() => File.Delete(archive));
            TryDelete(() =>
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            });
        }
    }

    /// <summary>
    /// Asks the plugin's repository whether its latest release is newer than what is installed.
    /// Null when it is not (or when the plugin has no known repository, or an unreadable version).
    /// </summary>
    /// <exception cref="PluginLoadException">The repository could not be asked; the message, meant for the user, says why.</exception>
    public async Task<PluginUpdate?> CheckForUpdateAsync(PluginInfo plugin, CancellationToken cancellation = default)
    {
        if (plugin.Id is null || plugin.Repository is null || PluginSource.Parse(plugin.Repository) is not { } source)
        {
            return null;
        }

        try
        {
            Package package = await FindPackageAsync(source, cancellation);
            return PluginOrigin.IsNewer(package.Tag, plugin.InstalledTag, plugin.Version)
                ? new PluginUpdate(plugin.Id, plugin.Name, plugin.Version, package.Tag, source)
                : null;
        }
        catch (HttpRequestException e)
        {
            throw new PluginLoadException(e.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{source} was not found on GitHub, or has not published a release.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub is not accepting more requests right now. Try again in a few minutes.",
                _ => "GitHub could not be reached: " + e.Message,
            }, e);
        }
        catch (TaskCanceledException e)
        {
            throw new PluginLoadException("GitHub did not answer in time.", e);
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for ApplyPendingUpdates to clear at the next start.
        }
    }

    private async Task<Package> FindPackageAsync(PluginSource repository, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, repository.LatestReleaseUrl);

        // GitHub rejects API requests without a user agent.
        request.Headers.UserAgent.ParseAdd("Notch-Plugins");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();

        try
        {
            JsonNode? release = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
            string tag = (string?)release?["tag_name"] ?? "";
            Package[] packages = [.. (release?["assets"] as JsonArray ?? [])
                .Select(asset => new Package(
                    tag,
                    (string?)asset?["name"] ?? "",
                    (string?)asset?["browser_download_url"] ?? "",
                    (long?)asset?["size"] ?? 0,
                    (string?)asset?["digest"]))
                .Where(p => p.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)

                    // Code is only ever taken from GitHub's own release downloads.
                    && p.Url.StartsWith("https://github.com/", StringComparison.Ordinal))];

            return packages.Length switch
            {
                1 => packages[0],
                0 => throw new PluginLoadException($"The latest release of {repository} ({tag}) has no .zip attached, so there is no plugin to install."),
                _ => throw new PluginLoadException($"The latest release of {repository} ({tag}) has {packages.Length} .zip files attached; a plugin release must have exactly one."),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new PluginLoadException($"GitHub's answer about {repository} could not be read.", e);
        }
    }

    private async Task DownloadAsync(Package package, string path, CancellationToken cancellation)
    {
        if (package.Size > MaxDownloadBytes)
        {
            throw new PluginLoadException($"{package.Name} is larger than the {MaxDownloadBytes / (1024 * 1024)} MB a plugin may be.");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, package.Url))
        {
            request.Headers.UserAgent.ParseAdd("Notch-Plugins");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
            await using FileStream file = File.Create(path);

            // Copied by hand so a download that lies about its size is cut off at the limit.
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellation)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new InvalidDataException("the download is larger than a plugin may be.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
            }
        }

        long size = new FileInfo(path).Length;
        if (size == 0 || (package.Size > 0 && size != package.Size))
        {
            throw new InvalidDataException($"the download is {size} bytes, but the release lists {package.Size}.");
        }

        if (package.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using FileStream file = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
            if (!actual.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("the download does not match the digest the release lists for it.");
            }
        }
    }

    /// <summary>Unpacks the plugin folder from the archive into <paramref name="staging"/> and returns its manifest.</summary>
    private static PluginManifest Unpack(string archive, string staging)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);

        // The plugin's files are either at the top of the archive or inside its only folder,
        // which is how "zip this folder" and GitHub's own tools tend to pack things.
        string[] manifests = [.. zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n == PluginManifest.FileName
                || (n.EndsWith("/" + PluginManifest.FileName, StringComparison.Ordinal) && n.Count(c => c == '/') == 1))];
        if (manifests.Length != 1)
        {
            throw new PluginLoadException(manifests.Length == 0
                ? $"The download has no {PluginManifest.FileName} at its top level or in a single folder, so it is not a Notch plugin."
                : $"The download holds more than one plugin; a release must contain exactly one {PluginManifest.FileName}.");
        }

        string prefix = manifests[0][..^PluginManifest.FileName.Length];
        if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
        {
            throw new PluginLoadException("The download unpacks to more than a plugin may be.");
        }

        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/'))
            {
                continue;
            }

            // An entry named "../x" must not be able to write outside the plugin's folder.
            string destination = Path.GetFullPath(Path.Combine(root, name[prefix.Length..]));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginLoadException("The download contains files that point outside the plugin's folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        PluginManifest manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(root, PluginManifest.FileName)));
        if (!File.Exists(Path.Combine(root, manifest.Assembly)))
        {
            throw new PluginLoadException($"The download does not contain {manifest.Assembly}, which its {PluginManifest.FileName} names.");
        }

        return manifest;
    }

    /// <summary>Moves the unpacked plugin into the plugins folder. True when that has to wait for a restart.</summary>
    private bool Place(string staging, PluginManifest manifest)
    {
        // Replace the copy that is already installed, whatever its folder is called.
        string folder = InstalledFolderOf(manifest.Id) ?? manifest.Id;
        string target = Path.Combine(pluginsDirectory, folder);
        string update = Path.Combine(pluginsDirectory, UpdatePrefix + folder);
        if (Directory.Exists(update))
        {
            Directory.Delete(update, recursive: true);
        }

        if (Directory.Exists(target))
        {
            // A loaded plugin keeps reading libraries from its folder as it needs them, so
            // swapping the files under it would mix two versions.
            if (isLoaded?.Invoke(manifest.Id) == true)
            {
                Directory.Move(staging, update);
                return true;
            }

            string old = Path.Combine(pluginsDirectory, OldPrefix + folder);
            try
            {
                // Moved aside in one step rather than deleted file by file, which could stop
                // half way on a file something has open and leave a broken plugin.
                if (Directory.Exists(old))
                {
                    Directory.Delete(old, recursive: true);
                }

                Directory.Move(target, old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Directory.Move(staging, update);
                return true;
            }

            TryDelete(() => Directory.Delete(old, recursive: true));
        }

        Directory.Move(staging, target);
        return false;
    }

    private string? InstalledFolderOf(string pluginId)
    {
        foreach (string directory in Directory.GetDirectories(pluginsDirectory))
        {
            string name = Path.GetFileName(directory);
            string manifestPath = Path.Combine(directory, PluginManifest.FileName);
            if (name.StartsWith('.') || !File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                if (PluginManifest.Parse(File.ReadAllText(manifestPath)).Id == pluginId)
                {
                    return name;
                }
            }
            catch (Exception e) when (e is PluginLoadException or IOException or UnauthorizedAccessException)
            {
                // Not a usable plugin, so not the one being replaced.
            }
        }

        return null;
    }

    private sealed record Package(string Tag, string Name, string Url, long Size, string? Digest);
}
