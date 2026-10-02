using System.Text.Json;

namespace Notch.Core.Clipboard;

public enum ClipboardKind
{
    Text,
    Image,
    Files,
}

/// <summary>One thing that was copied.</summary>
/// <param name="Text">The text, for <see cref="ClipboardKind.Text"/>.</param>
/// <param name="Image">PNG bytes, for <see cref="ClipboardKind.Image"/>.</param>
/// <param name="Files">Full paths, for <see cref="ClipboardKind.Files"/>.</param>
/// <param name="Source">The name of the program it was copied from, when known.</param>
public sealed record ClipboardItem(
    long Id,
    ClipboardKind Kind,
    DateTime CopiedAt,
    string? Text = null,
    byte[]? Image = null,
    IReadOnlyList<string>? Files = null,
    string? Source = null,
    bool Pinned = false)
{
    /// <summary>One line for the list: the first line of the text, the file names, or the picture's size.</summary>
    public string Preview => Kind switch
    {
        ClipboardKind.Text => FirstLine(Text ?? ""),
        ClipboardKind.Files => string.Join(", ", (Files ?? []).Select(f => Path.GetFileName(f.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : f)),
        _ => $"Picture ({(Image?.Length ?? 0) / 1024} KB)",
    };

    /// <summary>Whether two items hold the same thing, so copying it again just moves it to the top.</summary>
    public bool SameContentAs(ClipboardItem other) => Kind == other.Kind && Kind switch
    {
        ClipboardKind.Text => string.Equals(Text, other.Text, StringComparison.Ordinal),
        ClipboardKind.Files => (Files ?? []).SequenceEqual(other.Files ?? [], StringComparer.OrdinalIgnoreCase),
        _ => (Image ?? []).AsSpan().SequenceEqual(other.Image ?? []),
    };

    private static string FirstLine(string text)
    {
        string trimmed = text.TrimStart();
        int end = trimmed.IndexOfAny(['\r', '\n']);
        string line = (end < 0 ? trimmed : trimmed[..end]).Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>
/// What was copied lately, newest first. Kept in memory only: the history is gone when Notch quits,
/// except for what the user pinned, which <see cref="ClipboardPins"/> saves.
/// </summary>
public sealed class ClipboardHistory
{
    public const int DefaultCapacity = 50;
    public const int MaxTextLength = 100_000;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxFiles = 50;

    private readonly object _gate = new();
    private readonly List<ClipboardItem> _items = [];
    private readonly int _capacity;
    private long _nextId = 1;

    public ClipboardHistory(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    /// <summary>Raised on whichever thread changed the history.</summary>
    public event Action? Changed;

    /// <summary>Adds what was copied. Returns the stored item, or null when it is not worth keeping (empty, or too large).</summary>
    public ClipboardItem? AddText(string? text, DateTime now, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return null;
        }

        return Add(new ClipboardItem(0, ClipboardKind.Text, now, Text: text, Source: source));
    }

    public ClipboardItem? AddImage(byte[]? png, DateTime now, string? source = null)
    {
        if (png is not { Length: > 0 } || png.Length > MaxImageBytes)
        {
            return null;
        }

        return Add(new ClipboardItem(0, ClipboardKind.Image, now, Image: png, Source: source));
    }

    public ClipboardItem? AddFiles(IEnumerable<string>? paths, DateTime now, string? source = null)
    {
        string[] files = [.. (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxFiles)];
        return files.Length == 0 ? null : Add(new ClipboardItem(0, ClipboardKind.Files, now, Files: files, Source: source));
    }

    /// <summary>Puts items the user pinned in an earlier run back, below nothing newer.</summary>
    public void Restore(IEnumerable<ClipboardItem> pinned)
    {
        lock (_gate)
        {
            foreach (ClipboardItem item in pinned)
            {
                _items.Add(item with { Id = _nextId++, Pinned = true });
            }
        }

        Changed?.Invoke();
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || _items[index].Pinned == pinned)
            {
                return;
            }

            _items[index] = _items[index] with { Pinned = pinned };
            Trim();
        }

        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(i => i.Id == id) == 0)
            {
                return;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets everything except pinned items, or everything when <paramref name="includePinned"/>.</summary>
    public void Clear(bool includePinned = false)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(i => includePinned || !i.Pinned) == 0)
            {
                return;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Pinned items first, then the rest newest first.</summary>
    public IReadOnlyList<ClipboardItem> Snapshot(string? filter = null)
    {
        lock (_gate)
        {
            IEnumerable<ClipboardItem> items = _items;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string needle = filter.Trim();
                items = items.Where(i => Matches(i, needle));
            }

            return [.. items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.CopiedAt).ThenByDescending(i => i.Id)];
        }
    }

    public ClipboardItem? Find(long id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    private ClipboardItem Add(ClipboardItem item)
    {
        ClipboardItem stored;
        lock (_gate)
        {
            // Copying the same thing again moves it up (and keeps it pinned) instead of listing it twice.
            ClipboardItem? same = _items.FirstOrDefault(i => i.SameContentAs(item));
            if (same is not null)
            {
                _items.Remove(same);
                stored = same with { CopiedAt = item.CopiedAt, Source = item.Source ?? same.Source };
            }
            else
            {
                stored = item with { Id = _nextId++ };
            }

            _items.Add(stored);
            Trim();
        }

        Changed?.Invoke();
        return stored;
    }

    /// <summary>Drops the oldest unpinned items beyond the capacity.</summary>
    private void Trim()
    {
        while (_items.Count(i => !i.Pinned) > _capacity)
        {
            ClipboardItem oldest = _items.Where(i => !i.Pinned).OrderBy(i => i.CopiedAt).ThenBy(i => i.Id).First();
            _items.Remove(oldest);
        }
    }

    private static bool Matches(ClipboardItem item, string needle) => item.Kind switch
    {
        ClipboardKind.Text => item.Text!.Contains(needle, StringComparison.OrdinalIgnoreCase),
        ClipboardKind.Files => (item.Files ?? []).Any(f => f.Contains(needle, StringComparison.OrdinalIgnoreCase)),
        _ => "picture image".Contains(needle, StringComparison.OrdinalIgnoreCase),
    };
}

/// <summary>Whether a copy is meant to stay out of clipboard histories, and which programs never go in.</summary>
public static class ClipboardPrivacy
{
    /// <summary>Programs whose copies are never kept, as a safety net for password managers that do not mark them.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedPrograms =
    [
        "keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "nordpass", "roboform", "proton pass", "protonpass",
    ];

    /// <summary>
    /// Password managers and a few other programs mark what they put on the clipboard so that
    /// histories skip it: the format <c>ExcludeClipboardContentFromMonitorProcessing</c>, or
    /// <c>CanIncludeInClipboardHistory</c> set to 0.
    /// </summary>
    public static bool ShouldSkip(bool hasExcludeFormat, int? canIncludeInHistory, string? sourceProgram, IEnumerable<string>? excludedPrograms = null)
    {
        if (hasExcludeFormat || canIncludeInHistory == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(sourceProgram))
        {
            return false;
        }

        return (excludedPrograms ?? DefaultExcludedPrograms).Any(p => sourceProgram.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The only part of the history that survives a restart: what the user pinned.</summary>
public static class ClipboardPins
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static IReadOnlyList<ClipboardItem> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(file), Options) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string file, IEnumerable<ClipboardItem> items)
    {
        try
        {
            ClipboardItem[] pinned = [.. items.Where(i => i.Pinned)];
            if (pinned.Length == 0)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(pinned, Options));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pins are a convenience; failing to keep them must not take the app down.
        }
    }
}
