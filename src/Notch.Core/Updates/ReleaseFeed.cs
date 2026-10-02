using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Updates;

/// <param name="Sha256">Lower-case hex digest GitHub reports for the installer, or null if it reports none.</param>
/// <param name="SignatureUrl">The installer's <c>.sig</c> file on the same release, or null when it has none.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string InstallerUrl, long InstallerSize, string? Sha256, string? SignatureUrl = null);

/// <summary>Reads the project's GitHub releases to find installers to update to.</summary>
public static class ReleaseFeed
{
    public const string Repository = "Brick-Bread/WNotch";

    /// <summary>GitHub's "latest release" endpoint: never a draft or a pre-release.</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    // An update is an executable that gets run unattended, so it is only ever taken from
    // this repository's own release downloads.
    private const string TrustedDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    private const string InstallerPrefix = "Notch-Setup-";

    /// <summary>Parses a GitHub release object. Null when it has no version or no installer to offer.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            JsonNode? release = JsonNode.Parse(json);
            string? tag = (string?)release?["tag_name"];
            if (tag is null || !TryParseVersion(tag, out Version? version) || release?["assets"] is not JsonArray assets)
            {
                return null;
            }

            foreach (JsonNode? asset in assets)
            {
                string name = (string?)asset?["name"] ?? "";
                string url = (string?)asset?["browser_download_url"] ?? "";
                if (!name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !url.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string? digest = (string?)asset?["digest"];
                string? sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..].ToLowerInvariant()
                    : null;

                // The signature is a small file next to the installer, named after it.
                string signatureName = name + ".sig";
                string? signatureUrl = assets
                    .Select(a => (Name: (string?)a?["name"] ?? "", Url: (string?)a?["browser_download_url"] ?? ""))
                    .FirstOrDefault(a => a.Name.Equals(signatureName, StringComparison.OrdinalIgnoreCase)
                        && a.Url.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal)).Url;

                return new ReleaseInfo(version, tag, url, (long?)asset?["size"] ?? 0, sha256, string.IsNullOrEmpty(signatureUrl) ? null : signatureUrl);
            }

            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Reads "v1.2.3" or "1.2.3"; a pre-release suffix such as "-beta" is ignored.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        if (Version.TryParse(text, out Version? parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>Drops the fourth component so 1.2.3 and 1.2.3.0 compare equal.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
