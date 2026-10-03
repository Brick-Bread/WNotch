using System.Text.Json;

namespace Notch.Core.Telemetry;

/// <summary>
/// Opt-in record of how Notch is used, one JSON object per line so it loads straight into a
/// spreadsheet, pandas or any charting tool. Nothing is written unless <see cref="Enabled"/> is
/// set, and nothing leaves the computer: there is no upload, and no text the user typed, file
/// name, path or window title is ever recorded, only event names and counts.
/// </summary>
public sealed class UsageLog
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public UsageLog(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Off until the user turns it on in the settings.</summary>
    public bool Enabled { get; set; }

    public string FilePath => _path;

    /// <summary>Adds one event. Never throws: a full disk must not cost the user their notch.</summary>
    public void Record(string name, IReadOnlyDictionary<string, string>? details = null)
    {
        if (!Enabled)
        {
            return;
        }

        var entry = new Dictionary<string, object>
        {
            ["time"] = _now().ToString("O"),
            ["event"] = name,
        };
        if (details is not null)
        {
            foreach ((string key, string value) in details)
            {
                entry[key] = value;
            }
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Telemetry is optional; the app runs the same without it.
        }
    }

    /// <summary>Deletes everything recorded so far.</summary>
    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                File.Delete(_path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
