using Notch.Core.Agents;

namespace Notch.Core.Terminal;

/// <summary>
/// Reads the launcher buttons of the Terminal tab from settings. A line is
/// <c>Name = [VARIABLE=value ...] command [arguments]</c>, for example
/// <c>Work Claude = CLAUDE_CONFIG_DIR=C:\work\.claude claude</c>. Arguments with spaces go in double quotes.
/// A command with "claude" or "codex" in its name is treated as that agent, so forks get status tracking too.
/// </summary>
public static class TerminalPresets
{
    /// <summary>As many buttons as fit beside the folder button.</summary>
    public const int MaxPresets = 6;

    /// <summary>The presets a settings list describes: unreadable lines skipped, no more than fit, and the defaults when nothing is left.</summary>
    public static IReadOnlyList<TerminalProfile> FromLines(IEnumerable<string> lines)
    {
        var profiles = new List<TerminalProfile>();
        foreach (string line in lines)
        {
            if (profiles.Count < MaxPresets && TryParse(line, profiles.Select(p => p.Id), out TerminalProfile profile))
            {
                profiles.Add(profile);
            }
        }

        return profiles.Count > 0 ? profiles : FromLines(TerminalProfile.DefaultPresets);
    }

    /// <param name="takenIds">Ids already in use, so that two presets with the same name stay apart.</param>
    public static bool TryParse(string line, IEnumerable<string> takenIds, out TerminalProfile profile)
    {
        profile = null!;
        int equals = line.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        string name = line[..equals].Trim();
        List<string>? words = Split(line[(equals + 1)..]);
        if (name.Length == 0 || words is not { Count: > 0 })
        {
            return false;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int next = 0;
        while (next < words.Count - 1 && TryReadVariable(words[next], out string variable, out string value))
        {
            environment[variable] = value;
            next++;
        }

        string command = words[next];
        AgentKind agent = AgentFor(command);
        string id = UniqueId(name, takenIds);
        string hint = $"{command} was not found on your PATH. Check the command for \"{name}\" in the settings.";

        profile = new TerminalProfile(
            id, name, command, GlyphFor(agent), agent, hint, [.. words.Skip(next + 1)], environment);
        return true;
    }

    /// <summary>Back to the line a profile came from, for showing it in the settings.</summary>
    public static string ToLine(TerminalProfile profile)
    {
        var parts = new List<string>();
        parts.AddRange((profile.Environment ?? new Dictionary<string, string>()).Select(v => Quote($"{v.Key}={v.Value}")));
        parts.Add(Quote(profile.Command));
        parts.AddRange((profile.Arguments ?? []).Select(Quote));
        return $"{profile.DisplayName} = {string.Join(' ', parts)}";
    }

    private static AgentKind AgentFor(string command)
    {
        string file = Path.GetFileNameWithoutExtension(command);
        if (file.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return AgentKind.Claude;
        }

        return file.Contains("codex", StringComparison.OrdinalIgnoreCase) ? AgentKind.Codex : AgentKind.None;
    }

    private static string GlyphFor(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "\uE99A",
        AgentKind.Codex => "\uE943",
        _ => "\uE756",
    };

    private static string UniqueId(string name, IEnumerable<string> takenIds)
    {
        string slug = string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        if (slug.Length == 0)
        {
            slug = "terminal";
        }

        HashSet<string> taken = [.. takenIds];
        string id = slug;
        for (int n = 2; !taken.Add(id); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }

    /// <summary>NAME=value, where NAME looks like an environment variable rather than a path or a flag.</summary>
    private static bool TryReadVariable(string word, out string name, out string value)
    {
        int equals = word.IndexOf('=');
        name = equals > 0 ? word[..equals] : "";
        value = equals > 0 ? word[(equals + 1)..] : "";
        return name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static List<string>? Split(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inWord = false;
        bool quoted = false;

        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (inWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        if (quoted)
        {
            return null;
        }

        if (inWord)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string Quote(string word) =>
        word.Length == 0 || word.Any(char.IsWhiteSpace) ? $"\"{word}\"" : word;
}
