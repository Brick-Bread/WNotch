using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Notch.Core.Automation;
using Notch.Core.Settings;

namespace Notch.Core.Tests;

/// <summary>
/// Records what Notch does with the inputs in <c>tests/fixtures</c>, so the Rust port can be held to
/// the same answers: its tests read the same input and expected files. A failure here means the C#
/// behaviour changed. If that was meant, run the tests with <c>NOTCH_UPDATE_FIXTURES=1</c> to
/// rewrite the expected files, and look at the difference before committing it.
/// </summary>
public class FixtureTests
{
    /// <summary>Stands in for a widget id Notch made up, which is random each run.</summary>
    private const string GeneratedId = "*generated*";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string Fixtures = FindFixtures();

    public static TheoryData<string> SettingsInputs() =>
        [.. Directory.GetFiles(Path.Combine(Fixtures, "settings", "input"), "*.json").Select(Path.GetFileName).Order()!];

    [Theory]
    [MemberData(nameof(SettingsInputs))]
    public void Settings_load_and_save_as_recorded(string name)
    {
        string input = Path.Combine(Fixtures, "settings", "input", name);
        AppSettings settings = new SettingsStore(input).Load();
        Check(Path.Combine("settings", "expected", name), SaveToText(settings, File.ReadAllText(input)));
    }

    [Fact]
    public void A_missing_settings_file_gives_the_defaults()
    {
        AppSettings settings = new SettingsStore(Path.Combine(Path.GetTempPath(), $"notch-{Guid.NewGuid():N}", "settings.json")).Load();
        Check(Path.Combine("settings", "expected", "missing-file.json"), SaveToText(settings, ""));
    }

    [Fact]
    public void Commands_parse_as_recorded()
    {
        var results = new JsonArray();
        foreach (JsonNode? input in JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "commands", "input.json")))!.AsArray())
        {
            bool ok;
            NotchCommand? command;
            string? error;
            if (input!["url"] is { } url)
            {
                ok = CommandParser.TryParseUrl(url.GetValue<string>(), out command, out error);
            }
            else
            {
                ok = CommandParser.TryParseArguments([.. input["args"]!.AsArray().Select(a => a!.GetValue<string>())], out command, out error);
            }

            var result = new JsonObject { ["input"] = input.DeepClone(), ["ok"] = ok };
            if (ok)
            {
                result["command"] = Describe(command!);
            }
            else
            {
                result["error"] = error;
            }

            results.Add(result);
        }

        Check(Path.Combine("commands", "expected.json"), results.ToJsonString(Indented));
    }

    /// <summary>A command as plain JSON: its kind and its values. Durations are in seconds, colours #rrggbb.</summary>
    private static JsonObject Describe(NotchCommand command) => command switch
    {
        InstallPluginCommand c => new() { ["kind"] = "install", ["pluginId"] = c.PluginId },
        SetPluginEnabledCommand c => new() { ["kind"] = "setPluginEnabled", ["pluginId"] = c.PluginId, ["enabled"] = c.Enabled },
        StartTimerCommand c => new() { ["kind"] = "startTimer", ["seconds"] = c.Duration.TotalSeconds },
        TimerActionCommand c => new() { ["kind"] = "timerAction", ["action"] = c.Action.ToString().ToLowerInvariant() },
        OpenTabCommand c => new() { ["kind"] = "openTab", ["tab"] = c.Tab },
        OpenPageCommand c => new() { ["kind"] = "openPage", ["page"] = c.Page },
        ToastCommand c => new()
        {
            ["kind"] = "toast",
            ["title"] = c.Title,
            ["detail"] = c.Detail,
            ["glyph"] = c.Glyph,
            ["color"] = c.Color is { } color ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : null,
            ["lifetimeSeconds"] = c.Lifetime?.TotalSeconds,
        },
        OpenPaletteCommand => new() { ["kind"] = "openPalette" },
        OpenSettingsCommand => new() { ["kind"] = "openSettings" },
        _ => throw new InvalidOperationException($"No description for {command.GetType().Name}; add one here and in the Rust tests."),
    };

    /// <summary>
    /// The settings as <see cref="SettingsStore"/> writes them. Widget ids that are not in the input
    /// were made up by Notch and differ each run, so they are written as <see cref="GeneratedId"/>.
    /// </summary>
    private static string SaveToText(AppSettings settings, string input)
    {
        string file = Path.Combine(Path.GetTempPath(), $"notch-{Guid.NewGuid():N}", "settings.json");
        try
        {
            new SettingsStore(file).Save(settings);
            JsonNode saved = JsonNode.Parse(File.ReadAllText(file))!;
            if (saved["HomeWidgets"] is JsonArray widgets)
            {
                foreach (JsonNode? widget in widgets)
                {
                    if (widget?["Id"] is { } id && !input.Contains($"\"{id.GetValue<string>()}\"", StringComparison.Ordinal))
                    {
                        widget["Id"] = GeneratedId;
                    }
                }
            }

            return saved.ToJsonString(Indented);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    private static void Check(string relative, string actual)
    {
        string path = Path.Combine(Fixtures, relative);
        actual = actual.ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("NOTCH_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"{relative} is missing. Run the tests with NOTCH_UPDATE_FIXTURES=1 to write it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string FindFixtures()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/fixtures was not found above " + AppContext.BaseDirectory);
    }
}
