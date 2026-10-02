namespace Notch.Core.Shell;

/// <summary>Ranks the command palette's entries against what was typed.</summary>
public static class PaletteSearch
{
    public const int DefaultMax = 8;

    /// <summary>
    /// The entries that match every word of <paramref name="query"/>, best first; the first
    /// <paramref name="max"/> entries in their given order when nothing was typed. A word matches
    /// at the start of the title or of one of its words, anywhere in it, or as letters in order
    /// ("tm" finds "Timer"); the keywords count a little less than the title.
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(IReadOnlyList<T> items, string? query, Func<T, string> title, Func<T, string?>? keywords = null, int max = DefaultMax)
    {
        string[] words = (query ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [.. items.Take(max)];
        }

        var scored = new List<(T Item, int Score, int Index)>();
        for (int index = 0; index < items.Count; index++)
        {
            T item = items[index];
            string name = title(item).ToLowerInvariant();
            string extra = (keywords?.Invoke(item) ?? "").ToLowerInvariant();

            int total = 0;
            foreach (string word in words)
            {
                int score = Math.Max(Score(word.ToLowerInvariant(), name), Score(word.ToLowerInvariant(), extra) * 7 / 10);
                if (score == 0)
                {
                    total = 0;
                    break;
                }

                total += score;
            }

            if (total > 0)
            {
                scored.Add((item, total, index));
            }
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Take(max).Select(s => s.Item)];
    }

    private static int Score(string word, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (text.StartsWith(word, StringComparison.Ordinal))
        {
            return 100;
        }

        int at = text.IndexOf(word, StringComparison.Ordinal);
        if (at > 0 && !char.IsLetterOrDigit(text[at - 1]))
        {
            return 80;
        }

        if (at >= 0)
        {
            return 50;
        }

        // The letters in order, with a small bonus the closer together they are.
        int position = 0;
        int first = -1;
        foreach (char c in word)
        {
            int found = text.IndexOf(c, position);
            if (found < 0)
            {
                return 0;
            }

            first = first < 0 ? found : first;
            position = found + 1;
        }

        int span = position - first;
        return Math.Max(5, 30 - (span - word.Length));
    }
}
