using System.Text.Json;
using Notch.Core.Automation;

namespace Notch.Core.Plugins;

/// <summary>One plugin in the marketplace list on the website.</summary>
public sealed record RegistryEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>owner/repo</c> on GitHub: where the plugin is installed from.</summary>
    public required string Repository { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>What the plugin says it uses; see <see cref="PluginManifest.Permissions"/>.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>The plugin API the latest release was written for, when the registry says.</summary>
    public int? ApiVersion { get; init; }

    /// <summary>A maintainer of the registry looked at this plugin's code.</summary>
    public bool Verified { get; init; }
}

/// <summary>
/// The list of plugins the website offers (<c>plugins/registry.json</c>). The app uses it to turn
/// a plugin id from a <c>notch://install</c> link into the repository to install from, so a link
/// can never point Notch at code the registry has not listed.
/// </summary>
public sealed class PluginRegistry(IReadOnlyList<RegistryEntry> entries)
{
    public const string DefaultUrl = "https://brick-bread.github.io/WNotch/plugins/registry.json";

    /// <summary>The most entries read from a registry, so a huge or hostile file cannot swamp the app.</summary>
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<RegistryEntry> Entries { get; } = entries;

    public RegistryEntry? Find(string id) =>
        Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>
    /// Reads a registry file: <c>{ "plugins": [ { "id", "name", "repository", ... } ] }</c>. Entries
    /// that are not usable (bad id, no repository, duplicate id) are skipped; the rest are kept.
    /// </summary>
    /// <exception cref="PluginLoadException">The text is not a registry at all.</exception>
    public static PluginRegistry Parse(string json)
    {
        Raw? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(json, Options);
        }
        catch (JsonException e)
        {
            throw new PluginLoadException("The plugin list could not be read: " + e.Message, e);
        }

        if (raw?.Plugins is null)
        {
            throw new PluginLoadException("The plugin list has no \"plugins\".");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<RegistryEntry>();
        foreach (RawEntry item in raw.Plugins.Take(MaxEntries))
        {
            if (item is null || !CommandParser.IsPluginId(item.Id) || string.IsNullOrWhiteSpace(item.Name)
                || PluginSource.Parse(item.Repository ?? "") is not { } source || !seen.Add(item.Id!))
            {
                continue;
            }

            entries.Add(new RegistryEntry
            {
                Id = item.Id!,
                Name = Clip(item.Name, 80)!,
                Repository = source.ToString(),
                Author = Clip(item.Author, 80),
                Description = Clip(item.Description, 400),
                Tags = [.. (item.Tags ?? []).Select(t => Clip(t, 24)).OfType<string>().Take(8)],
                Permissions = PluginManifest.CleanPermissions(item.Permissions),
                ApiVersion = item.ApiVersion,
                Verified = item.Verified,
            });
        }

        return new PluginRegistry(entries);
    }

    /// <summary>
    /// The current registry: fetched from <paramref name="url"/> and remembered in
    /// <paramref name="cacheFile"/>, or read from the remembered copy when the website cannot be reached.
    /// </summary>
    /// <exception cref="PluginLoadException">Neither the website nor a remembered copy could be read.</exception>
    public static async Task<PluginRegistry> LoadAsync(HttpClient http, string cacheFile, string url = DefaultUrl, CancellationToken cancellation = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Notch-Plugins");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024)
            {
                throw new PluginLoadException("The plugin list is too large.");
            }

            string text = await response.Content.ReadAsStringAsync(cancellation);
            PluginRegistry registry = Parse(text);
            Remember(cacheFile, text);
            return registry;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (Recall(cacheFile) is { } cached)
            {
                return cached;
            }

            throw new PluginLoadException("The plugin list could not be loaded: " + e.Message, e);
        }
    }

    private static void Remember(string cacheFile, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.WriteAllText(cacheFile, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only an offline fallback; losing it is fine.
        }
    }

    private static PluginRegistry? Recall(string cacheFile)
    {
        try
        {
            return File.Exists(cacheFile) ? Parse(File.ReadAllText(cacheFile)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PluginLoadException)
        {
            return null;
        }
    }

    private static string? Clip(string? text, int length)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > length ? text[..length] : text;
    }

    private sealed class Raw
    {
        public List<RawEntry>? Plugins { get; set; }
    }

    private sealed class RawEntry
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Repository { get; set; }

        public string? Author { get; set; }

        public string? Description { get; set; }

        public string[]? Tags { get; set; }

        public string[]? Permissions { get; set; }

        public int? ApiVersion { get; set; }

        public bool Verified { get; set; }
    }
}
