using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notch.Core.Plugins;

/// <summary>A plugin could not be read or loaded. The message is shown to the user in settings.</summary>
public sealed class PluginLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The contents of a plugin's <c>plugin.json</c>, which describes it to Notch before any of its code runs.</summary>
public sealed partial record PluginManifest
{
    public const string FileName = "plugin.json";

    private const int MaxIdLength = 64;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Unique and permanent, e.g. "acme.build-status": lowercase letters and digits in groups
    /// separated by dots or dashes. Names the plugin's data folder and prefixes its activity ids.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The name shown in settings.</summary>
    public required string Name { get; init; }

    /// <summary>File name of the plugin's main assembly, in the same folder as the manifest.</summary>
    public required string Assembly { get; init; }

    /// <summary>The <see cref="PluginApi.Version"/> the plugin was written for.</summary>
    public required int ApiVersion { get; init; }

    /// <summary>The plugin's own version, for display only.</summary>
    public string? Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>Where the plugin is published, as "owner/repo", so Notch can look for updates to a copy that was not installed from GitHub. Optional.</summary>
    public string? Repository { get; init; }

    /// <summary>Options the user can change in Notch's Settings window. Optional.</summary>
    public IReadOnlyList<PluginSettingField> Settings { get; init; } = [];

    /// <summary>
    /// What the plugin says it uses, such as "network" or "filesystem", shown before it is installed
    /// or switched on. This is a statement by the plugin's author, not a limit: a plugin is .NET
    /// code running as the user and can do anything the user can.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>The permissions Notch knows by name, for the settings list and the install window.</summary>
    public static IReadOnlyDictionary<string, string> KnownPermissions { get; } = new Dictionary<string, string>
    {
        ["network"] = "Connects to the internet",
        ["filesystem"] = "Reads or writes files outside its own folder",
        ["terminal"] = "Starts or reads terminal sessions",
        ["shell"] = "Runs other programs",
        ["notifications"] = "Reads your notifications",
        ["clipboard"] = "Reads or writes the clipboard",
        ["audio"] = "Listens to the loudness of what your PC is playing (levels only, never the sound)",
    };

    /// <summary>A short description of a permission as declared, or the word itself when it is not a known one.</summary>
    public static string DescribePermission(string permission) =>
        KnownPermissions.TryGetValue(permission, out string? text) ? text : permission;

    /// <summary>Tidies a declared list: lowercase words, no duplicates, no empty or absurdly long entries.</summary>
    public static IReadOnlyList<string> CleanPermissions(IEnumerable<string>? declared) =>
        declared is null
            ? []
            : [.. declared
                .Select(p => (p ?? "").Trim().ToLowerInvariant())
                .Where(p => p.Length is > 0 and <= 32)
                .Distinct()
                .Take(12)];

    /// <summary>Reads and validates a manifest.</summary>
    /// <exception cref="PluginLoadException">The text is not a usable manifest; the message says why.</exception>
    public static PluginManifest Parse(string json)
    {
        Raw? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(json, Options);
        }
        catch (JsonException e)
        {
            throw new PluginLoadException($"{FileName} could not be read: {e.Message}", e);
        }

        if (raw is null)
        {
            throw new PluginLoadException($"{FileName} is empty.");
        }

        string id = Require(raw.Id, "id");
        if (id.Length > MaxIdLength || !IdPattern().IsMatch(id))
        {
            throw new PluginLoadException(
                $"The id '{id}' is not valid: use lowercase letters and digits, separated by dots or dashes, at most {MaxIdLength} characters.");
        }

        string assembly = Require(raw.Assembly, "assembly");
        if (Path.GetFileName(assembly) != assembly || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginLoadException($"\"assembly\" must be the file name of a .dll next to {FileName}.");
        }

        int apiVersion = raw.ApiVersion ?? throw new PluginLoadException($"{FileName} is missing \"apiVersion\".");
        if (apiVersion < 1)
        {
            throw new PluginLoadException("\"apiVersion\" must be 1 or higher.");
        }

        if (apiVersion > PluginApi.Version)
        {
            throw new PluginLoadException(
                $"Needs plugin API {apiVersion}, but this version of Notch offers {PluginApi.Version}. Update Notch to use it.");
        }

        return new PluginManifest
        {
            Id = id,
            Name = Require(raw.Name, "name"),
            Assembly = assembly,
            ApiVersion = apiVersion,
            Version = Optional(raw.Version),
            Author = Optional(raw.Author),
            Description = Optional(raw.Description),
            Repository = Optional(raw.Repository),
            Settings = PluginSettingField.ParseAll(raw.Settings),
            Permissions = CleanPermissions(raw.Permissions),
        };
    }

    private static string Require(string? value, string property) =>
        Optional(value) ?? throw new PluginLoadException($"{FileName} is missing \"{property}\".");

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    private sealed class Raw
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Assembly { get; set; }

        public int? ApiVersion { get; set; }

        public string? Version { get; set; }

        public string? Author { get; set; }

        public string? Description { get; set; }

        public string? Repository { get; set; }

        public JsonElement? Settings { get; set; }

        public string[]? Permissions { get; set; }
    }
}
