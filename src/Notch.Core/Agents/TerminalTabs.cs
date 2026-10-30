namespace Notch.Core.Agents;

/// <summary>
/// Picks the Windows Terminal tab an agent runs in. Windows Terminal has no way to ask which tab a
/// program is in, but a tab is named after the title of the console in it, which Notch can read for the
/// agent; failing that, after the folder, which shells put in their title. Pure logic: reading the tabs
/// and the title is the platform's.
/// </summary>
public static class TerminalTabs
{
    /// <summary>
    /// The index of the tab in <paramref name="tabNames"/> that shows the agent, or -1 when no tab, or more
    /// than one, fits: then only the window is brought up, rather than a tab that may be the wrong one.
    /// </summary>
    /// <param name="consoleTitle">The title of the agent's console, when it could be read.</param>
    /// <param name="folderName">The name of the folder the agent works in, when known.</param>
    public static int Pick(IReadOnlyList<string> tabNames, string? consoleTitle, string? folderName)
    {
        string title = (consoleTitle ?? "").Trim();
        if (title.Length > 0)
        {
            // Several tabs with the agent's very title: no telling which, and a looser look would not tell either.
            int exact = Single(tabNames, name => string.Equals(name.Trim(), title, StringComparison.Ordinal));
            if (exact != None)
            {
                return Math.Max(exact, -1);
            }

            // A tab may add to the title (a bell mark, a profile's own text) or cut a long one short.
            int close = Single(tabNames, name => Overlaps(name.Trim(), title));
            if (close != None)
            {
                return Math.Max(close, -1);
            }
        }

        string folder = (folderName ?? "").Trim();
        return folder.Length >= 3
            ? Math.Max(Single(tabNames, name => name.Contains(folder, StringComparison.OrdinalIgnoreCase)), -1)
            : -1;
    }

    private const int None = -1;

    private const int Several = -2;

    private static bool Overlaps(string name, string title)
    {
        const int least = 4;
        if (name.Length < least)
        {
            return false;
        }

        // A name cut short ends in an ellipsis, which the title does not have.
        string cut = name.TrimEnd('…', '.', ' ');
        return title.Contains(name, StringComparison.Ordinal)
            || name.Contains(title, StringComparison.Ordinal)
            || (cut.Length >= least && title.StartsWith(cut, StringComparison.Ordinal));
    }

    /// <summary>The index of the one name that fits, <see cref="None"/>, or <see cref="Several"/>.</summary>
    private static int Single(IReadOnlyList<string> names, Func<string, bool> fits)
    {
        int found = None;
        for (int i = 0; i < names.Count; i++)
        {
            if (fits(names[i]))
            {
                if (found != None)
                {
                    return Several;
                }

                found = i;
            }
        }

        return found;
    }
}
