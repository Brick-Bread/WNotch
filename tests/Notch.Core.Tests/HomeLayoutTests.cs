using System.Text.Json;
using Notch.Core.Settings;
using Notch.Core.Widgets;

namespace Notch.Core.Tests;

public class HomeLayoutTests
{
    private static HomeWidget Widget(string kind, WidgetWidth width, string? id = null) =>
        new() { Kind = kind, Width = width, Id = id ?? HomeWidget.NewId() };

    private static string[][] Kinds(IReadOnlyList<IReadOnlyList<HomeWidget>> rows) =>
        [.. rows.Select(r => r.Select(w => w.Kind).ToArray())];

    [Fact]
    public void Default_layout_is_what_home_showed_before()
    {
        IReadOnlyList<IReadOnlyList<HomeWidget>> rows = HomeLayout.Rows(HomeLayout.Default());

        Assert.Equal(
            [[WidgetKinds.Media], [WidgetKinds.Agents], [WidgetKinds.Timer, WidgetKinds.Calendar]],
            Kinds(rows));
    }

    [Fact]
    public void Half_widgets_pair_up_and_a_lone_one_keeps_its_own_row()
    {
        HomeWidget[] widgets =
        [
            Widget(WidgetKinds.Clock, WidgetWidth.Half),
            Widget(WidgetKinds.Media, WidgetWidth.Full),
            Widget(WidgetKinds.Note, WidgetWidth.Half),
            Widget(WidgetKinds.Note, WidgetWidth.Half),
            Widget(WidgetKinds.Weather, WidgetWidth.Half),
        ];

        Assert.Equal(
            [[WidgetKinds.Clock], [WidgetKinds.Media], [WidgetKinds.Note, WidgetKinds.Note], [WidgetKinds.Weather]],
            Kinds(HomeLayout.Rows(widgets)));
    }

    [Fact]
    public void Hidden_widgets_take_no_room_so_their_neighbours_pair()
    {
        HomeWidget[] widgets =
        [
            Widget(WidgetKinds.Timer, WidgetWidth.Half),
            Widget(WidgetKinds.Clipboard, WidgetWidth.Half),
            Widget(WidgetKinds.Calendar, WidgetWidth.Half),
        ];

        Assert.Equal(
            [[WidgetKinds.Timer, WidgetKinds.Calendar]],
            Kinds(HomeLayout.Rows(widgets, w => w.Kind != WidgetKinds.Clipboard)));
    }

    [Fact]
    public void Normalize_drops_unknown_kinds_and_second_singles_and_fixes_ids()
    {
        List<HomeWidget> widgets = HomeLayout.Normalize(
        [
            Widget(WidgetKinds.Timer, WidgetWidth.Half, "a"),
            Widget("hologram", WidgetWidth.Full, "b"),
            Widget(WidgetKinds.Timer, WidgetWidth.Half, "c"),
            Widget(WidgetKinds.Note, WidgetWidth.Half, "a"),
            Widget(WidgetKinds.Note, WidgetWidth.Half, ""),
            null,
        ]);

        Assert.Equal([WidgetKinds.Timer, WidgetKinds.Note, WidgetKinds.Note], widgets.Select(w => w.Kind));
        Assert.Equal(3, widgets.Select(w => w.Id).Distinct().Count());
        Assert.Equal("a", widgets[0].Id);
        Assert.All(widgets, w => Assert.False(string.IsNullOrWhiteSpace(w.Id)));
    }

    [Fact]
    public void Addable_leaves_out_singles_already_on_home()
    {
        IReadOnlyList<WidgetType> addable = HomeLayout.Addable(HomeLayout.Default());

        Assert.DoesNotContain(addable, t => t.Kind == WidgetKinds.Timer);
        Assert.DoesNotContain(addable, t => t.Kind == WidgetKinds.Media);
        Assert.Contains(addable, t => t.Kind == WidgetKinds.Note);
        Assert.Contains(addable, t => t.Kind == WidgetKinds.Shelf);
    }

    [Fact]
    public void Move_puts_a_widget_in_its_new_place()
    {
        List<HomeWidget> widgets = HomeLayout.Default();
        string timer = widgets[2].Id;

        Assert.True(HomeLayout.Move(widgets, timer, 0));
        Assert.Equal([WidgetKinds.Timer, WidgetKinds.Media, WidgetKinds.Agents, WidgetKinds.Calendar], widgets.Select(w => w.Kind));
        Assert.False(HomeLayout.Move(widgets, timer, 0));
        Assert.True(HomeLayout.Move(widgets, timer, 99));
        Assert.Equal(WidgetKinds.Timer, widgets[^1].Kind);
        Assert.False(HomeLayout.Move(widgets, "missing", 0));
    }

    [Fact]
    public void Options_fall_back_to_their_defaults()
    {
        var clock = new HomeWidget { Kind = WidgetKinds.Clock };
        Assert.False(clock.Flag("seconds"));
        clock.Options["seconds"] = WidgetOption.On;
        Assert.True(clock.Flag("seconds"));

        var stats = new HomeWidget { Kind = WidgetKinds.Stats };
        Assert.True(stats.Flag("cpu"));
        Assert.Equal("", stats.Option("nonsense"));
    }

    [Fact]
    public void Settings_without_a_layout_get_the_default_and_one_round_trips()
    {
        AppSettings fresh = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(HomeLayout.Default().Select(w => w.Kind), fresh.HomeWidgets.Select(w => w.Kind));

        fresh.HomeWidgets = [Widget(WidgetKinds.Note, WidgetWidth.Full, "n1")];
        fresh.HomeWidgets[0].Options[WidgetCatalog.NoteTextKey] = "milk";
        AppSettings back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(fresh))!;

        HomeWidget note = Assert.Single(back.HomeWidgets);
        Assert.Equal(("n1", WidgetWidth.Full, "milk"), (note.Id, note.Width, note.Option(WidgetCatalog.NoteTextKey)));
        Assert.Contains("\"Full\"", JsonSerializer.Serialize(fresh));
    }

    [Fact]
    public void Every_catalog_kind_is_distinct_and_choices_have_valid_defaults()
    {
        Assert.Equal(WidgetCatalog.All.Count, WidgetCatalog.All.Select(t => t.Kind).Distinct().Count());
        foreach (WidgetOption option in WidgetCatalog.All.SelectMany(t => t.Options).Where(o => o is { Type: WidgetOptionType.Choice, Choices: not null }))
        {
            Assert.Contains(option.Default, option.Choices!);
        }
    }

    [Fact]
    public void Widget_buttons_read_names_targets_and_links()
    {
        IReadOnlyList<WidgetButton> buttons = WidgetButtons.Parse(
            "Notes = C:\\Tools\\notes.exe\r\n\r\n# a comment\nhttps://example.com/?q=a=b\nC:\\Users\\me\\Projects\\\nBuild = dotnet build -c Release\n = \n");

        Assert.Equal(
            [
                new WidgetButton("Notes", "C:\\Tools\\notes.exe"),
                new WidgetButton("example.com", "https://example.com/?q=a=b"),
                new WidgetButton("Projects", "C:\\Users\\me\\Projects\\"),
                new WidgetButton("Build", "dotnet build -c Release"),
            ],
            buttons);
    }

    [Fact]
    public void Feed_text_follows_a_json_path_or_strips_html()
    {
        const string json = """{"data":{"status":"All good"},"items":[{"title":"First"},{"title":"Second"}],"count":3}""";
        Assert.Equal("All good", FeedText.Extract(json, "data.status"));
        Assert.Equal("Second", FeedText.Extract(json, "items.1.title"));
        Assert.Equal("3", FeedText.Extract(json, "count"));
        Assert.Throws<FormatException>(() => FeedText.Extract(json, "data.missing"));
        Assert.Throws<FormatException>(() => FeedText.Extract("not json", "a"));

        Assert.Equal("Hello & welcome", FeedText.Extract("<html><style>p{}</style><p>Hello &amp;</p>\n<b>welcome</b></html>", null));
        Assert.Equal("plain", FeedText.Extract("  plain \n", ""));
        Assert.EndsWith("…", FeedText.Extract(new string('x', 2000), null));
    }

    [Fact]
    public void Clock_zones_are_found_by_id_or_city()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("Tokyo Standard Time", TimeSpan.FromHours(9), "(UTC+09:00) Osaka, Sapporo, Tokyo", "Tokyo Standard Time");
        TimeZoneInfo london = TimeZoneInfo.CreateCustomTimeZone("GMT Standard Time", TimeSpan.Zero, "(UTC+00:00) Dublin, Edinburgh, Lisbon, London", "GMT Standard Time");
        TimeZoneInfo[] zones = [tokyo, london];

        Assert.Null(ClockZones.Find(" ", zones));
        Assert.Same(TimeZoneInfo.Utc, ClockZones.Find("utc", zones));
        Assert.Equal(tokyo.Id, ClockZones.Find("tokyo", zones)?.Id);
        Assert.Equal(london.Id, ClockZones.Find("GMT Standard Time", zones)?.Id);
        Assert.Null(ClockZones.Find("Atlantis", zones));
    }

    [Fact]
    public void Open_meteo_answers_are_read()
    {
        WeatherPlace? place = OpenMeteo.ReadPlace("""{"results":[{"name":"London","latitude":51.5085,"longitude":-0.12574,"country_code":"GB"}]}""");
        Assert.Equal(new WeatherPlace("London, GB", 51.5085, -0.12574), place);
        Assert.Null(OpenMeteo.ReadPlace("""{"generationtime_ms":0.5}"""));

        WeatherReport report = OpenMeteo.ReadReport("""
            {"current":{"temperature_2m":12.3,"weather_code":61,"wind_speed_10m":14.0,"is_day":0},
             "daily":{"temperature_2m_max":[15.1],"temperature_2m_min":[8.4]}}
            """);
        Assert.Equal(new WeatherReport(12.3, 15.1, 8.4, 61, 14.0, false), report);
        Assert.Equal("Rain", OpenMeteo.Describe(report.Code));
        Assert.Throws<FormatException>(() => OpenMeteo.ReadReport("{}"));

        WeatherForecast forecast = OpenMeteo.ReadForecast("""
            {"current":{"time":"2026-10-23T14:45","temperature_2m":12.3,"apparent_temperature":10.1,"relative_humidity_2m":81,
                        "weather_code":2,"wind_speed_10m":14.0,"wind_direction_10m":225,"is_day":1},
             "hourly":{"time":["2026-10-23T13:00","2026-10-23T14:00","2026-10-23T15:00"],
                       "temperature_2m":[11.0,12.0,13.5],"weather_code":[1,2,61],"precipitation_probability":[0,null,70],"is_day":[1,1,1]},
             "daily":{"time":["2026-10-23","2026-10-24"],"weather_code":[61,0],"temperature_2m_max":[15.1,17.0],"temperature_2m_min":[8.4,9.0],
                      "precipitation_probability_max":[80,5],"sunrise":["2026-10-23T07:31","2026-10-24T07:33"],
                      "sunset":["2026-10-23T17:52","2026-10-24T17:50"],"uv_index_max":[1.5,2.25]}}
            """);
        Assert.Equal((10.1, 81, "SW"), (forecast.FeelsLike, forecast.Humidity, OpenMeteo.Compass(forecast.WindDirection)));
        Assert.Equal([new DateTime(2026, 10, 23, 14, 0, 0), new DateTime(2026, 10, 23, 15, 0, 0)], forecast.Hours.Select(h => h.Time));
        Assert.Equal([null, 70], forecast.Hours.Select(h => h.RainChance));
        Assert.Equal(2, forecast.Days.Count);
        Assert.Equal(new WeatherDay(new DateOnly(2026, 10, 23), 15.1, 8.4, 61, 80, new DateTime(2026, 10, 23, 7, 31, 0), new DateTime(2026, 10, 23, 17, 52, 0), 1.5), forecast.Days[0]);
        Assert.Equal(("N", "N", "E"), (OpenMeteo.Compass(350), OpenMeteo.Compass(-10), OpenMeteo.Compass(95)));

        string url = OpenMeteo.ForecastUrl(place!, fahrenheit: true);
        Assert.Contains("latitude=51.5085&longitude=-0.1257", url);
        Assert.Contains("temperature_unit=fahrenheit", url);
        Assert.Contains("name=San%20Francisco", OpenMeteo.GeocodeUrl(" San Francisco "));
    }
}
