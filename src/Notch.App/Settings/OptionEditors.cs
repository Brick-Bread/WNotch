using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Notch.Core.Plugins;

namespace Notch.App.Settings;

/// <summary>
/// The controls for the options a plugin lists in its manifest, shown under the plugin in the
/// Settings window. Reads back only what the user changed.
/// </summary>
internal sealed class OptionEditors
{
    private readonly List<Editor> _editors = [];

    public OptionEditors(PluginInfo plugin, IReadOnlyDictionary<string, JsonElement> current)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (PluginSettingField field in plugin.Settings ?? [])
        {
            current.TryGetValue(field.Key, out JsonElement value);
            Editor editor = Create(field, current.ContainsKey(field.Key) ? value : null);
            _editors.Add(editor);

            if (field.Type != PluginSettingType.Bool)
            {
                panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 8, 0, 3) });
            }

            panel.Children.Add(editor.Control);
            if (field.Description is not null)
            {
                panel.Children.Add(new TextBlock { Text = field.Description, Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            }
        }

        Element = new Expander
        {
            Header = "Options",
            Margin = new Thickness(24, 0, 0, 6),
            Content = panel,
        };
    }

    public Expander Element { get; }

    /// <summary>The values that differ from what was stored when the editors were made.</summary>
    public Dictionary<string, JsonElement> Changes()
    {
        Dictionary<string, JsonElement> changes = [];
        foreach (Editor editor in _editors)
        {
            if (editor.Read() is { } value && (editor.Original is not { } original || original.GetRawText() != value.GetRawText()))
            {
                changes[editor.Field.Key] = value;
            }
        }

        return changes;
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static Editor Create(PluginSettingField field, JsonElement? original)
    {
        switch (field.Type)
        {
            case PluginSettingType.Bool:
            {
                var box = new CheckBox
                {
                    Content = field.Label,
                    Margin = new Thickness(0, 8, 0, 0),
                    IsChecked = original is { ValueKind: JsonValueKind.True },
                };
                return new Editor(field, box, original, () => Json(box.IsChecked == true));
            }

            case PluginSettingType.Number:
            {
                var box = new TextBox { Text = original is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : "", ToolTip = RangeHint(field) };
                return new Editor(field, box, original, () =>
                {
                    if (!double.TryParse((box.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    {
                        return null;
                    }

                    number = Math.Clamp(number, field.Min ?? double.MinValue, field.Max ?? double.MaxValue);
                    return field.Step % 1 == 0 ? Json((long)Math.Round(number)) : Json(number);
                });
            }

            case PluginSettingType.Secret:
            {
                var box = new PasswordBox { ToolTip = "Leave empty to keep what is stored." };
                return new Editor(field, box, null, () => box.Password.Length == 0 ? null : Json(box.Password));
            }

            case PluginSettingType.Choice:
            {
                var box = new ComboBox { ItemsSource = field.Options };
                if (original is { ValueKind: JsonValueKind.String } selected && field.Options.Contains(selected.GetString()!))
                {
                    box.SelectedItem = selected.GetString();
                }

                return new Editor(field, box, original, () => box.SelectedItem is string choice ? Json(choice) : null);
            }

            case PluginSettingType.List:
            {
                string[] lines = original is { ValueKind: JsonValueKind.Array } list
                    ? [.. list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : [];
                var box = new TextBox
                {
                    Text = string.Join(Environment.NewLine, lines),
                    AcceptsReturn = true,
                    Height = 64,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    ToolTip = field.Hint ?? "One per line.",
                };
                return new Editor(field, box, original, () => Json((box.Text ?? "")
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }

            default:
            {
                var box = new TextBox { Text = original is { ValueKind: JsonValueKind.String } text ? text.GetString() : "", ToolTip = field.Hint };
                return new Editor(field, box, original, () => Json((box.Text ?? "").Trim()));
            }
        }
    }

    private static string? RangeHint(PluginSettingField field) =>
        field.Min is not null || field.Max is not null ? $"From {field.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"} to {field.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"}." : null;

    /// <param name="Read">The value to store, or null to leave the stored one alone.</param>
    private sealed record Editor(PluginSettingField Field, FrameworkElement Control, JsonElement? Original, Func<JsonElement?> Read);
}
