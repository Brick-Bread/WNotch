using System.Text;

namespace Notch.Core.Terminal;

/// <summary>Finds executables the way the shell would and builds CreateProcess command lines.</summary>
public static class CommandResolver
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>Resolves a bare command name through PATH and PATHEXT. Null when it is not installed.</summary>
    public static string? FindOnPath(string command, string? pathVariable, string? pathExt, Func<string, bool> fileExists)
    {
        string[] extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // npm also drops an extensionless shell script next to its .cmd shim, which Windows
        // cannot execute, so a bare name is only tried with a PATHEXT extension.
        bool hasExtension = extensions.Any(e => command.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> names = hasExtension ? [command] : extensions.Select(e => command + e);

        if (Path.IsPathRooted(command))
        {
            return names.FirstOrDefault(fileExists);
        }

        foreach (string directory in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static string? FindOnPath(string command) => FindOnPath(
        command,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        File.Exists);

    /// <summary>Builds a command line for CreateProcess. Batch files are run through cmd.exe, as they cannot be started directly.</summary>
    public static string BuildCommandLine(string executablePath, IEnumerable<string> arguments)
    {
        string direct = string.Join(' ', new[] { executablePath }.Concat(arguments).Select(QuoteArgument));

        string extension = Path.GetExtension(executablePath);
        bool isBatch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

        // With /s, cmd strips exactly the outer pair of quotes and runs the rest verbatim.
        return isBatch ? $"cmd.exe /d /s /c \"{direct}\"" : direct;
    }

    /// <summary>Quotes one argument following the rules the C runtime uses to split a command line.</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote must be doubled, then the quote itself escaped.
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            quoted.Append(c);
            backslashes = 0;
        }

        // Trailing backslashes precede the closing quote, so they are doubled too.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
