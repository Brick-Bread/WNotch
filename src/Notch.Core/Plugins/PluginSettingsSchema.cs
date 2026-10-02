using System.Text.Json;

namespace Notch.Core.Plugins;

public enum PluginSettingType
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>A number, whole unless <see cref="PluginSettingField.Step"/> says otherwise.</summary>
    Number,

    /// <summary>A tick box.</summary>
    Bool,

    /// <summary>Text that is hidden while typing and never shown again. Left empty, it keeps the stored value.</summary>
    Secret,

    /// <summary>One of <see cref="PluginSettingField.Options"/>.</summary>
    Choice,

    /// <summary>Several lines of text; stored as a JSON array of strings.</summary>
    List,
}

/// <summary>
/// An option the plugin lists in its <c>plugin.json</c> under "settings". Notch shows it in the
/// Settings window under the plugin and stores the value in the plugin's settings file, where
/// <see cref="IPluginSettings.Get{T}"/> finds it by <see cref="Key"/>.
/// </summary>
public sealed record PluginSettingField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public PluginSettingType Type { get; init; }

    /// <summary>One line under the label.</summary>
    public string? Description { get; init; }

    /// <summary>Greyed text in an empty box.</summary>
    public string? Hint { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>For numbers: how far the value may move at a time, e.g. 1 for whole numbers (the default).</summary>
    public double Step { get; init; } = 1;

    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Parses the manifest's "settings" array. Entries that cannot be used are skipped.</summary>
    internal static IReadOnlyList<PluginSettingField> ParseAll(JsonElement? array)
    {
        if (array is not { ValueKind: JsonValueKind.Array } list)
        {
            return [];
        }

        List<PluginSettingField> fields = [];
        foreach (JsonElement item in list.EnumerateArray().Take(40))
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("key", out JsonElement key) || key.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(key.GetString())
                || fields.Any(f => f.Key == key.GetString()!.Trim()))
            {
                continue;
            }

            string name = key.GetString()!.Trim();
            PluginSettingType type = Enum.TryParse(Text(item, "type"), ignoreCase: true, out PluginSettingType parsed) ? parsed : PluginSettingType.Text;
            string[] options = item.TryGetProperty("options", out JsonElement o) && o.ValueKind == JsonValueKind.Array
                ? [.. o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                : [];
            if (type == PluginSettingType.Choice && options.Length == 0)
            {
                continue;
            }

            fields.Add(new PluginSettingField
            {
                Key = name,
                Label = Text(item, "label") ?? name,
                Type = type,
                Description = Text(item, "description"),
                Hint = Text(item, "hint"),
                Min = Number(item, "min"),
                Max = Number(item, "max"),
                Step = Number(item, "step") is > 0 and var step ? step : 1,
                Options = options,
            });
        }

        return fields;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static double? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
