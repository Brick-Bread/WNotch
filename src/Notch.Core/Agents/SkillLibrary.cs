using System.IO;

namespace Notch.Core.Agents;

/// <summary>Whether a library entry is a skill or a subagent.</summary>
public enum LibraryItemKind
{
    Skill,
    Agent,
}

/// <summary>
/// A skill or subagent available to the coding agents, as read from a <c>.claude</c> folder.
/// </summary>
/// <param name="Scope">Where it was found: "User" for the home folder, else the project folder's name.</param>
public sealed record LibraryItem(LibraryItemKind Kind, string Name, string Description, string Scope);

/// <summary>
/// Finds the skills and subagents the coding agents (Claude Code and the like) would load, by
/// reading the <c>.claude/skills</c> and <c>.claude/agents</c> folders. A skill is a folder with a
/// <c>SKILL.md</c>; a subagent is a single <c>.md</c> file. Both open with a YAML front-matter block
/// carrying a name and description, which is what the lists show.
/// </summary>
public static class SkillLibrary
{
    /// <summary>
    /// Reads every skill and subagent under the given <c>.claude</c> folders. When a name repeats
    /// within a kind a later folder wins, matching how a project's own entries shadow the user's.
    /// Entries come back sorted by kind, then name.
    /// </summary>
    /// <param name="claudeDirs">Each a <c>.claude</c> folder and the scope label to show for what it holds.</param>
    public static IReadOnlyList<LibraryItem> Discover(IEnumerable<(string ClaudeDir, string Scope)> claudeDirs)
    {
        var byKey = new Dictionary<(LibraryItemKind, string), LibraryItem>();
        foreach ((string dir, string scope) in claudeDirs)
        {
            foreach (LibraryItem item in ReadSkills(Path.Combine(dir, "skills"), scope))
            {
                byKey[(item.Kind, item.Name)] = item;
            }

            foreach (LibraryItem item in ReadAgents(Path.Combine(dir, "agents"), scope))
            {
                byKey[(item.Kind, item.Name)] = item;
            }
        }

        return [.. byKey.Values
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One skill per sub-folder, named by its <c>SKILL.md</c> or, failing that, the folder.</summary>
    private static IEnumerable<LibraryItem> ReadSkills(string skillsDir, string scope)
    {
        foreach (string folder in SafeEntries(skillsDir, Directory.EnumerateDirectories))
        {
            string file = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }

            (string? name, string? description) = ReadMetadata(file);
            yield return new LibraryItem(
                LibraryItemKind.Skill,
                string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name,
                description ?? "",
                scope);
        }
    }

    /// <summary>One subagent per <c>.md</c> file, named by its front matter or, failing that, the file.</summary>
    private static IEnumerable<LibraryItem> ReadAgents(string agentsDir, string scope)
    {
        foreach (string file in SafeEntries(agentsDir, dir => Directory.EnumerateFiles(dir, "*.md")))
        {
            (string? name, string? description) = ReadMetadata(file);
            yield return new LibraryItem(
                LibraryItemKind.Agent,
                string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name,
                description ?? "",
                scope);
        }
    }

    /// <summary>Enumerates a folder, treating a missing or unreadable one as empty.</summary>
    private static IReadOnlyList<string> SafeEntries(string dir, Func<string, IEnumerable<string>> enumerate)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            return [.. enumerate(dir)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static (string? Name, string? Description) ReadMetadata(string file)
    {
        try
        {
            return ParseFrontMatter(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Pulls <c>name</c> and <c>description</c> out of a leading <c>---</c> YAML block. Only the two
    /// top-level keys are read; a block scalar (<c>&gt;</c> or <c>|</c>) folds its indented lines into one.
    /// </summary>
    internal static (string? Name, string? Description) ParseFrontMatter(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        if (i >= lines.Length || lines[i].Trim() != "---")
        {
            return (null, null);
        }

        string? name = null;
        string? description = null;
        for (i++; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            // Indented lines belong to the value above, which the block-scalar reader already took.
            if (line.Length == 0 || line[0] is ' ' or '\t')
            {
                continue;
            }

            if (TryKey(line, "name", out string nameValue))
            {
                name = ReadValue(nameValue, lines, ref i);
            }
            else if (TryKey(line, "description", out string descriptionValue))
            {
                description = ReadValue(descriptionValue, lines, ref i);
            }
        }

        return (name, description);
    }

    /// <summary>Whether the line is <c>key:</c> at the start, and if so the text after the colon.</summary>
    private static bool TryKey(string line, string key, out string value)
    {
        value = "";
        if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line[(key.Length + 1)..].Trim();
        return true;
    }

    /// <summary>The value on the key's line, unquoted; or, for a block scalar, the indented lines folded together.</summary>
    private static string ReadValue(string inline, string[] lines, ref int i)
    {
        if (inline is ">" or "|" || inline.StartsWith("> ", StringComparison.Ordinal) || inline.StartsWith("| ", StringComparison.Ordinal)
            || inline is ">-" or "|-" or ">+" or "|+")
        {
            var parts = new List<string>();
            while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || lines[i + 1][0] is ' ' or '\t'))
            {
                i++;
                string folded = lines[i].Trim();
                if (folded.Length > 0)
                {
                    parts.Add(folded);
                }
            }

            return string.Join(' ', parts);
        }

        return Unquote(inline);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
