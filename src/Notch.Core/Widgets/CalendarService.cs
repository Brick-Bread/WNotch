namespace Notch.Core.Widgets;

/// <summary>Downloads the user's calendar feeds and keeps the next few days of events.</summary>
public sealed class CalendarService(HttpClient http, TimeProvider? time = null)
{
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(7);

    /// <summary>Deadlines are looked for further ahead than events, and a day back so one just missed still shows.</summary>
    private static readonly TimeSpan DeadlineHorizon = TimeSpan.FromDays(60);
    private static readonly TimeSpan DeadlineLookBack = TimeSpan.FromDays(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile IReadOnlyList<CalendarEntry> _upcoming = [];

    /// <summary>Events from now through the next week, earliest first.</summary>
    public IReadOnlyList<CalendarEntry> Upcoming => _upcoming;

    private volatile IReadOnlyList<Deadlines.Deadline> _deadlines = [];

    /// <summary>
    /// The feeds' deadlines (see <see cref="CalendarFeed.ReadDeadlines"/>) from a day ago through the next 60 days.
    /// A feed that failed to load keeps the deadlines it had, so a network hiccup does not drop them from the pill.
    /// </summary>
    public IReadOnlyList<Deadlines.Deadline> Deadlines => _deadlines;

    /// <summary>How many feeds failed to load in the last refresh.</summary>
    public int FailedFeeds { get; private set; }

    /// <summary>May be raised on any thread.</summary>
    public event EventHandler? Changed;

    public async Task RefreshAsync(IEnumerable<string> feedUrls, CancellationToken cancellation = default)
    {
        DateTimeOffset now = _time.GetLocalNow();
        var entries = new List<CalendarEntry>();
        var deadlines = new List<Deadlines.Deadline>();
        IReadOnlyList<Deadlines.Deadline> previous = _deadlines;
        int failed = 0;
        int feed = 0;

        foreach (string url in feedUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            int index = feed++;
            try
            {
                string text = await http.GetStringAsync(NormalizeUrl(url), cancellation);
                entries.AddRange(CalendarFeed.ReadEntries(text, now, now + Horizon).Select(e => e with { Feed = index }));
                deadlines.AddRange(CalendarFeed.ReadDeadlines(text, now - DeadlineLookBack, now + DeadlineHorizon, index));
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or UriFormatException or InvalidOperationException or TaskCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
                failed++;
                deadlines.AddRange(previous.Where(d => d.Id.StartsWith($"cal:{index}:", StringComparison.Ordinal)));
            }
        }

        FailedFeeds = failed;
        _upcoming = [.. entries.OrderBy(e => e.Start)];
        _deadlines = [.. deadlines.OrderBy(d => d.Due)];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Calendar apps hand out webcal:// links, which are plain HTTPS underneath.</summary>
    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["webcal://".Length..]
            : url;
    }
}
