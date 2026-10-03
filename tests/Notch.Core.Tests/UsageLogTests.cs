using System.Text.Json;
using Notch.Core.Telemetry;

namespace Notch.Core.Tests;

public sealed class UsageLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "notch-usage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Writes_nothing_until_enabled()
    {
        var log = new UsageLog(Path.Combine(_dir, "usage.jsonl"));
        log.Record("app_start");
        Assert.False(File.Exists(log.FilePath));
    }

    [Fact]
    public void Writes_one_json_object_per_line_with_the_details()
    {
        var log = new UsageLog(Path.Combine(_dir, "usage.jsonl"), () => new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)) { Enabled = true };
        log.Record("tab_opened", new Dictionary<string, string> { ["tab"] = "Stats" });
        log.Record("app_start");

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("tab_opened", first.RootElement.GetProperty("event").GetString());
        Assert.Equal("Stats", first.RootElement.GetProperty("tab").GetString());
        Assert.StartsWith("2026-10-03T12:00:00", first.RootElement.GetProperty("time").GetString());
    }

    [Fact]
    public void Clear_removes_the_file()
    {
        var log = new UsageLog(Path.Combine(_dir, "usage.jsonl")) { Enabled = true };
        log.Record("app_start");
        log.Clear();
        Assert.False(File.Exists(log.FilePath));
    }
}
