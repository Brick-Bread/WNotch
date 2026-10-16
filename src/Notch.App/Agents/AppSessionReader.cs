using System.IO;
using System.Text;
using Notch.Core.Agents;

namespace Notch.App.Agents;

/// <summary>One session of a desktop app, as its files say right now.</summary>
/// <param name="Key">Unique among all entries on the board.</param>
/// <param name="Viewed">When the app says the user last looked at it; null when the app does not keep that.</param>
internal sealed record AppSession(
    string Key,
    DesktopApp App,
    string? Title,
    string? Folder,
    TurnReading Turn,
    DateTime? Viewed,
    bool AwaitingPerson);

/// <summary>
/// Reads the sessions the Claude and ChatGPT desktop apps keep on disk: the Claude app's session
/// files with Claude Code's transcripts, and Codex's thread transcripts. Files that have not
/// changed since the last read are not read again, so reading every few seconds stays cheap.
/// </summary>
internal sealed class AppSessionReader
{
    /// <summary>How recently a Claude app session must have done something to be listed.</summary>
    private static readonly TimeSpan ClaudeRecent = TimeSpan.FromHours(6);

    /// <summary>How recently a ChatGPT thread must have done something to be listed. The app runs one server for all, so its threads cannot be counted.</summary>
    private static readonly TimeSpan ChatGptRecent = TimeSpan.FromHours(1);

    private const int MaxChatGptSessions = 5;

    /// <summary>How much of the end of a transcript is read; enough for the last few lines, however long a tool's output.</summary>
    private const int TailBytes = 256 * 1024;

    private readonly Dictionary<string, (DateTime Written, ClaudeAppSession? Session)> _claudeFiles = [];
    private readonly Dictionary<string, (DateTime Written, long Length, TurnReading Turn)> _turns = [];
    private readonly Dictionary<string, string> _transcriptPaths = [];
    private readonly Dictionary<string, CodexThreadMeta> _codexMeta = [];
    private (DateTime Written, Dictionary<string, string> Titles) _codexTitles = (DateTime.MinValue, []);

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ClaudeConfig => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(UserProfile, ".claude");

    private static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
        ? custom
        : Path.Combine(UserProfile, ".codex");

    /// <summary>The sessions of the apps that are running.</summary>
    public IReadOnlyList<AppSession> Read(IReadOnlyList<RunningApp> apps, DateTime now)
    {
        var result = new List<AppSession>();
        foreach (RunningApp app in apps)
        {
            try
            {
                result.AddRange(app.App == DesktopApp.Claude ? ReadClaude(app, now) : ReadChatGpt(now));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next scan.
            }
        }

        return result;
    }

    private IEnumerable<AppSession> ReadClaude(RunningApp app, DateTime now)
    {
        // The app runs one Claude Code program for every session it has open, so that many of the
        // latest sessions are the open ones.
        if (app.Workers == 0)
        {
            return [];
        }

        var sessions = new List<(ClaudeAppSession Session, string? Transcript, DateTime Last)>();
        var seen = new HashSet<string>();
        foreach (string root in ClaudeSessionFolders())
        {
            foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("local_*.json", SearchOption.AllDirectories))
            {
                seen.Add(file.FullName);
                if (now - file.LastWriteTimeUtc > ClaudeRecent)
                {
                    continue;
                }

                if (!_claudeFiles.TryGetValue(file.FullName, out var cached) || cached.Written != file.LastWriteTimeUtc)
                {
                    _claudeFiles[file.FullName] = cached = (file.LastWriteTimeUtc, AppSessions.ReadClaudeSession(ReadAll(file.FullName)));
                }

                if (cached.Session is not { Archived: false } session)
                {
                    continue;
                }

                string? transcript = FindTranscript(session.TranscriptId);
                DateTime last = Latest(session.LastActivity, transcript is null ? null : File.GetLastWriteTimeUtc(transcript));
                if (now - last <= ClaudeRecent)
                {
                    sessions.Add((session, transcript, last));
                }
            }
        }

        Forget(_claudeFiles, seen);
        // The packaged app's folder can be the roaming one seen through a second path.
        return sessions
            .OrderByDescending(s => s.Last)
            .DistinctBy(s => s.Session.Id)
            .Take(app.Workers)
            .Select(s => new AppSession(
                "app-claude-" + s.Session.Id,
                DesktopApp.Claude,
                s.Session.Title,
                s.Session.Folder,
                s.Transcript is null ? new TurnReading(TurnState.Unknown, s.Session.LastActivity) : Turn(s.Transcript, AppSessions.ReadClaudeTurn),
                s.Session.LastFocused,
                s.Session.AwaitingPerson))
            .ToArray();
    }

    private IEnumerable<AppSession> ReadChatGpt(DateTime now)
    {
        string sessions = Path.Combine(CodexHome, "sessions");
        if (!Directory.Exists(sessions))
        {
            return [];
        }

        Dictionary<string, string> titles = CodexTitles();
        var found = new List<(CodexThreadMeta Meta, string Path, DateTime Written)>();
        foreach (FileInfo file in new DirectoryInfo(sessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
        {
            if (now - file.LastWriteTimeUtc > ChatGptRecent)
            {
                continue;
            }

            if (!_codexMeta.TryGetValue(file.FullName, out CodexThreadMeta? meta))
            {
                // A thread whose first line is not written yet is looked at again next time.
                meta = AppSessions.ReadCodexMeta(ReadFirstLine(file.FullName));
                if (meta is not null)
                {
                    _codexMeta[file.FullName] = meta;
                }
            }

            // Threads of the Codex command line are found as running programs already.
            if (meta is { FromApp: true, Subagent: false })
            {
                found.Add((meta, file.FullName, file.LastWriteTimeUtc));
            }
        }

        return found
            .OrderByDescending(f => f.Written)
            .Take(MaxChatGptSessions)
            .Select(f => new AppSession(
                "app-chatgpt-" + f.Meta.Id,
                DesktopApp.ChatGpt,
                titles.GetValueOrDefault(f.Meta.Id),
                f.Meta.Folder,
                Turn(f.Path, AppSessions.ReadCodexTurn),
                null,
                false))
            .ToArray();
    }

    /// <summary>Where the Claude app keeps its session files: its roaming folder, or the packaged app's copy of it.</summary>
    private static IEnumerable<string> ClaudeSessionFolders()
    {
        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
        if (Directory.Exists(roaming))
        {
            yield return roaming;
        }

        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages))
        {
            yield break;
        }

        foreach (string package in Directory.EnumerateDirectories(packages, "Claude_*"))
        {
            string packaged = Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code-sessions");
            if (Directory.Exists(packaged))
            {
                yield return packaged;
            }
        }
    }

    /// <summary>The transcript Claude Code writes for a session: projects\&lt;folder&gt;\&lt;id&gt;.jsonl.</summary>
    private string? FindTranscript(string id)
    {
        if (_transcriptPaths.TryGetValue(id, out string? known) && File.Exists(known))
        {
            return known;
        }

        string projects = Path.Combine(ClaudeConfig, "projects");
        if (!Directory.Exists(projects))
        {
            return null;
        }

        foreach (string folder in Directory.EnumerateDirectories(projects))
        {
            string path = Path.Combine(folder, id + ".jsonl");
            if (File.Exists(path))
            {
                _transcriptPaths[id] = path;
                return path;
            }
        }

        return null;
    }

    private TurnReading Turn(string path, Func<IReadOnlyList<string>, TurnReading> read)
    {
        var file = new FileInfo(path);
        if (_turns.TryGetValue(path, out var cached) && cached.Written == file.LastWriteTimeUtc && cached.Length == file.Length)
        {
            return cached.Turn;
        }

        TurnReading turn = read(ReadTail(path));
        _turns[path] = (file.LastWriteTimeUtc, file.Length, turn);
        return turn;
    }

    private Dictionary<string, string> CodexTitles()
    {
        string path = Path.Combine(CodexHome, "session_index.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        DateTime written = File.GetLastWriteTimeUtc(path);
        if (_codexTitles.Written != written)
        {
            _codexTitles = (written, AppSessions.ReadCodexTitles(ReadAll(path).Split('\n')));
        }

        return _codexTitles.Titles;
    }

    /// <summary>The last lines of a file another program is still writing; the first, cut in half, is left out when the file is longer than what is read.</summary>
    private static IReadOnlyList<string> ReadTail(string path)
    {
        using FileStream stream = Open(path);
        long start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);

        string[] lines = Encoding.UTF8.GetString(buffer).Split('\n');
        return start > 0 ? lines[1..] : lines;
    }

    private static string ReadFirstLine(string path)
    {
        using var reader = new StreamReader(Open(path), Encoding.UTF8);
        return reader.ReadLine() ?? "";
    }

    private static string ReadAll(string path)
    {
        using var reader = new StreamReader(Open(path), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static DateTime Latest(DateTime? a, DateTime? b) =>
        new[] { a ?? DateTime.MinValue, b ?? DateTime.MinValue }.Max();

    private static void Forget<T>(Dictionary<string, T> cache, HashSet<string> keep)
    {
        foreach (string gone in cache.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            cache.Remove(gone);
        }
    }
}
