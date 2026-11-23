namespace Notch.Core.Deadlines;

/// <summary>Where a deadline came from, which decides what completing it does.</summary>
public enum DeadlineSource
{
    /// <summary>Typed into the notch. Completing it deletes it.</summary>
    Manual,

    /// <summary>A calendar event tagged as a deadline, or a task with a due date. Completing it hides it in Notch only.</summary>
    Calendar,

    /// <summary>Added by <c>notchctl</c>, a <c>notch://</c> link or the webhook. Kept like a manual one, keyed by its title.</summary>
    Link,

    /// <summary>Set by a plugin, which keeps it up to date. Completing it hides it until the plugin moves its due time.</summary>
    Plugin,
}

/// <summary>Something that has to be done by a certain moment.</summary>
/// <param name="Id">Unique across all sources: a manual one's own id, or the source's key with a prefix.</param>
/// <param name="Due">The moment it is due. A deadline for a whole day is due at the end of that day.</param>
/// <param name="Detail">A second line, such as the calendar's name or what the plugin says about it.</param>
/// <param name="Owner">For a plugin's deadline, the plugin's id.</param>
public sealed record Deadline(
    string Id,
    string Title,
    DateTimeOffset Due,
    DeadlineSource Source,
    string? Detail = null,
    string? Owner = null)
{
    public const int MaxTitleLength = 120;
    public const int MaxDetailLength = 200;

    /// <summary>Tells one due time of a deadline from another: hiding or snoozing applies to this one, not to a later move.</summary>
    public string Key => $"{Id}@{Due.UtcTicks}";

    /// <summary>One line, trimmed and cut to <paramref name="max"/> characters; empty for nothing at all.</summary>
    public static string Tidy(string? text, int max = MaxTitleLength)
    {
        text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > max ? text[..max].TrimEnd() : text;
    }
}

/// <summary>A deadline a plugin shows; see <see cref="Plugins.IPluginDeadlines"/>.</summary>
public sealed record PluginDeadline
{
    /// <summary>Stable per deadline and unique within the plugin. Setting the same id again replaces it.</summary>
    public required string Id { get; init; }

    /// <summary>What is due, e.g. "Submit the report". Up to 120 characters.</summary>
    public required string Title { get; init; }

    /// <summary>When it is due.</summary>
    public required DateTimeOffset Due { get; init; }

    /// <summary>A second line, such as the project's name. Optional.</summary>
    public string? Detail { get; init; }
}
