using System.Text.Json;

namespace Notch.Core.Widgets;

public sealed record TodoItem(string Text, bool Done);

/// <summary>
/// The items of a to-do widget: open ones first, in the user's order, then the ticked ones. Kept in the
/// widget's options as JSON (<see cref="ItemsKey"/>).
/// </summary>
public sealed class TodoList
{
    public const string ItemsKey = "items";
    public const int MaxItems = 100;
    public const int MaxLength = 300;

    private readonly List<TodoItem> _items;

    private TodoList(IEnumerable<TodoItem> items) => _items = [.. Ordered(items)];

    public IReadOnlyList<TodoItem> Items => _items;

    public bool AnyDone => _items.Any(i => i.Done);

    public int OpenCount => _items.Count(i => !i.Done);

    /// <summary>Reads what <see cref="ToJson"/> wrote. Anything unreadable is an empty list rather than an error.</summary>
    public static TodoList FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TodoList([]);
        }

        try
        {
            TodoItem?[] items = JsonSerializer.Deserialize<TodoItem?[]>(json) ?? [];
            return new TodoList(items.Where(i => i is { Text.Length: > 0 }).Select(i => i!).Take(MaxItems));
        }
        catch (JsonException)
        {
            return new TodoList([]);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(_items);

    /// <summary>Adds an open item after the other open ones. False for empty text or a full list.</summary>
    public bool Add(string text)
    {
        text = Tidy(text);
        if (text.Length == 0 || _items.Count >= MaxItems)
        {
            return false;
        }

        _items.Insert(OpenCount, new TodoItem(text, false));
        return true;
    }

    /// <summary>Ticks an open item, which goes to the top of the ticked ones, or unticks one, which goes to the end of the open ones.</summary>
    public void Toggle(int index)
    {
        if (!Valid(index))
        {
            return;
        }

        TodoItem item = _items[index];
        _items.RemoveAt(index);
        if (item.Done)
        {
            _items.Insert(OpenCount, item with { Done = false });
        }
        else
        {
            _items.Insert(OpenCount, item with { Done = true });
        }
    }

    public void Remove(int index)
    {
        if (Valid(index))
        {
            _items.RemoveAt(index);
        }
    }

    /// <summary>Changes an item's text; empty text removes it.</summary>
    public void Edit(int index, string text)
    {
        if (!Valid(index))
        {
            return;
        }

        text = Tidy(text);
        if (text.Length == 0)
        {
            _items.RemoveAt(index);
        }
        else
        {
            _items[index] = _items[index] with { Text = text };
        }
    }

    public void ClearDone() => _items.RemoveAll(i => i.Done);

    /// <summary>
    /// Moves an item to another place, counted as if it had already been taken out. It stays among its own
    /// kind: an open item never lands below a ticked one, nor a ticked one above an open one.
    /// </summary>
    public bool Move(int from, int to)
    {
        if (!Valid(from))
        {
            return false;
        }

        TodoItem item = _items[from];
        _items.RemoveAt(from);
        int open = OpenCount;
        to = item.Done ? Math.Clamp(to, open, _items.Count) : Math.Clamp(to, 0, open);
        _items.Insert(to, item);
        return to != from;
    }

    private bool Valid(int index) => index >= 0 && index < _items.Count;

    private static string Tidy(string text)
    {
        // One line: a pasted paragraph becomes one item.
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() : text;
    }

    private static IEnumerable<TodoItem> Ordered(IEnumerable<TodoItem> items)
    {
        List<TodoItem> all = [.. items];
        return all.Where(i => !i.Done).Concat(all.Where(i => i.Done));
    }
}
