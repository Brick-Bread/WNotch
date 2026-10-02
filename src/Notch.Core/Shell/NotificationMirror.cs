using Notch.Core.Activities;

namespace Notch.Core.Shell;

/// <summary>A Windows notification as the watcher reads it.</summary>
/// <param name="Title">The first line of the toast: usually the sender or the subject.</param>
/// <param name="Body">The rest of its text. Never shown; kept out of the pill on purpose.</param>
public sealed record NotificationInfo(long Id, string App, string Title, string? Body);

/// <summary>Which notifications reach the pill, and how they look there.</summary>
public static class NotificationMirror
{
    public const string ActivityPrefix = "notification.";
    public const int MaxTitleLength = 80;
    public const int MaxAppLength = 40;

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether an app's notifications are shown: all of them when <paramref name="allowedApps"/> is
    /// empty, otherwise only those from an app whose name contains one of the listed words.
    /// </summary>
    public static bool Allows(string app, IReadOnlyCollection<string> allowedApps) =>
        allowedApps.Count == 0
        || allowedApps.Any(allowed => !string.IsNullOrWhiteSpace(allowed) && app.Contains(allowed.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The pill's short notice for a notification: the sender or subject as the title and the app
    /// as the detail. The message itself stays out of the pill, where anyone looking at the screen could read it.
    /// </summary>
    public static Activity? ToActivity(NotificationInfo notification)
    {
        string title = Clip(notification.Title, MaxTitleLength);
        string app = Clip(notification.App, MaxAppLength);
        if (title.Length == 0 && app.Length == 0)
        {
            return null;
        }

        return new Activity
        {
            Id = ActivityPrefix + notification.Id,
            Tier = ActivityTier.Transient,
            Title = title.Length > 0 ? title : app,
            Detail = title.Length > 0 && app.Length > 0 ? app : null,
            Glyph = "",
            Glow = new Glow(GlowColor.Blue, GlowPattern.Flash, 0.8),
            Lifetime = Lifetime,
        };
    }

    private static string Clip(string? text, int length)
    {
        string single = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length > length ? single[..(length - 1)] + "…" : single;
    }
}

/// <summary>Remembers which notifications were already seen, so a poll only reports new ones.</summary>
public sealed class NotificationDiff
{
    private readonly HashSet<long> _seen = [];
    private bool _primed;

    /// <summary>
    /// The notifications in <paramref name="current"/> that have not been seen. The first call only
    /// learns what is already there and reports nothing, so switching the feature on does not replay the action centre.
    /// </summary>
    public IReadOnlyList<NotificationInfo> Fresh(IReadOnlyList<NotificationInfo> current)
    {
        var fresh = new List<NotificationInfo>();
        foreach (NotificationInfo notification in current)
        {
            if (_seen.Add(notification.Id) && _primed)
            {
                fresh.Add(notification);
            }
        }

        // Forget ones that were dismissed, so the set does not grow forever and an id that comes back counts as new.
        _seen.IntersectWith(current.Select(n => n.Id));
        _primed = true;
        return fresh;
    }

    public void Reset()
    {
        _seen.Clear();
        _primed = false;
    }
}
