using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Notch.Core.Agents;
using Notch.Core.Settings;
using Notch.Platform.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Keeps the figures the usage widget shows: Claude's plan limits (only when the user switched that on),
/// Codex's from its transcripts, and what opencode spent today from its plugin's reports. Claude and Codex
/// are read again when the widget is on screen and they are due, never in the background.
/// </summary>
internal sealed class UsageService : IDisposable
{
    private static readonly TimeSpan ClaudeEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaudeAfterRefusal = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodexEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

    /// <summary>How many of the newest Codex transcripts are looked through for limits; a run of <c>codex exec</c> has none.</summary>
    private const int CodexFilesToTry = 6;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AgentHookServer _hooks;
    private readonly AppSettings _settings;
    private readonly string _openCodeFile;
    private readonly object _gate = new();
    private readonly DailyUsage _openCode;
    private DateTime _openCodeSaved = DateTime.MinValue;
    private DateTime _claudeDue = DateTime.MinValue;
    private DateTime _codexDue = DateTime.MinValue;
    private bool _refreshing;

    /// <param name="openCodeFile">Where today's opencode figures are kept between runs.</param>
    public UsageService(AgentHookServer hooks, AppSettings settings, string openCodeFile)
    {
        _hooks = hooks;
        _settings = settings;
        _openCodeFile = openCodeFile;
        _openCode = DailyUsage.Load(ReadText(openCodeFile));
        _hooks.MessageReceived += OnMessage;
    }

    /// <summary>Raised on whichever thread learnt something new.</summary>
    public event Action? Changed;

    public UsageReading? Claude { get; private set; }

    /// <summary>Why Claude's figures are missing or old, in words for the widget; null when they are fine.</summary>
    public string? ClaudeProblem { get; private set; }

    public UsageReading? Codex { get; private set; }

    /// <summary>What opencode spent today, or null when it has not reported today.</summary>
    public DailyUsage? OpenCodeToday
    {
        get
        {
            lock (_gate)
            {
                return _openCode.HasToday(DateTime.Now) ? DailyUsage.Load(_openCode.Save()) : null;
            }
        }
    }

    public void Dispose()
    {
        _hooks.MessageReceived -= OnMessage;
        SaveOpenCode(force: true);
    }

    /// <summary>Reads what is due again. Cheap to call often: it does nothing until something is due.</summary>
    public async Task RefreshAsync()
    {
        DateTime now = DateTime.UtcNow;
        bool claude = _settings.ClaudeUsage && now >= _claudeDue;
        bool codex = now >= _codexDue;
        if (_refreshing || (!claude && !codex))
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (codex)
            {
                _codexDue = now + CodexEvery;
                Codex = await Task.Run(() => ReadCodex(now)) ?? Codex;
            }

            if (claude)
            {
                await RefreshClaudeAsync(now);
            }
            else if (!_settings.ClaudeUsage)
            {
                Claude = null;
                ClaudeProblem = null;
            }

            Changed?.Invoke();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Asks again at the next chance, as after the setting was switched on.</summary>
    public void RefreshSoon() => _claudeDue = _codexDue = DateTime.MinValue;

    private async Task RefreshClaudeAsync(DateTime now)
    {
        _claudeDue = now + ClaudeEvery;
        string credentials = Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ".credentials.json");

        if (UsageMeters.ReadClaudeSignIn(ReadText(credentials)) is not { } signIn)
        {
            ClaudeProblem = "Sign in to Claude Code to see Claude's limits";
            return;
        }

        // Claude Code renews its sign-in while it runs; Notch never does, so it cannot get in Claude Code's way.
        if (signIn.Expires is { } expires && expires <= now)
        {
            ClaudeProblem = Claude is null ? "Claude's sign-in has run out; it renews when Claude Code runs" : "Last read " + Claude.At.ToLocalTime().ToString("t");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageMeters.ClaudeUsageAddress);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signIn.Token);
            request.Headers.Add("anthropic-beta", UsageMeters.ClaudeBeta);

            // The address turns away requests that do not look like Claude Code's.
            request.Headers.UserAgent.ParseAdd("claude-code/2.1 (Notch)");
            using HttpResponseMessage response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _claudeDue = now + ClaudeAfterRefusal;
                ClaudeProblem = response.StatusCode == HttpStatusCode.TooManyRequests ? "Claude asked Notch to ask less often" : "Claude did not accept the sign-in";
                return;
            }

            response.EnsureSuccessStatusCode();
            UsageReading? reading = UsageMeters.ReadClaude(await response.Content.ReadAsStringAsync(), now);
            Claude = reading ?? Claude;
            ClaudeProblem = reading is null ? "Claude's answer could not be read" : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            ClaudeProblem = Claude is null ? "Could not reach Claude" : "Last read " + Claude.At.ToLocalTime().ToString("t");
        }
    }

    /// <summary>The latest limits in the newest Codex transcripts that have any.</summary>
    private static UsageReading? ReadCodex(DateTime now)
    {
        string sessions = Path.Combine(
            Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            "sessions");

        try
        {
            if (!Directory.Exists(sessions))
            {
                return null;
            }

            // Laid out by date (YYYY\MM\DD), so the newest are in the last folders; a few days back is plenty.
            IEnumerable<FileInfo> newest = new DirectoryInfo(sessions)
                .EnumerateDirectories().OrderByDescending(d => d.Name).Take(2)
                .SelectMany(year => year.EnumerateDirectories().OrderByDescending(d => d.Name).Take(2))
                .SelectMany(month => month.EnumerateDirectories().OrderByDescending(d => d.Name).Take(3))
                .SelectMany(day => day.EnumerateFiles("rollout-*.jsonl"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(CodexFilesToTry);

            foreach (FileInfo file in newest)
            {
                if (UsageMeters.ReadCodex(Tail(file.FullName), now) is { } reading)
                {
                    return reading;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Codex is writing, or the folder is not ours to read; the last figures stay.
        }

        return null;
    }

    /// <summary>The last lines of a transcript, which is where its latest figures are.</summary>
    private static List<string> Tail(string path)
    {
        const int bytes = 256 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - bytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private void OnMessage(AgentHooks.HookMessage message)
    {
        if (message.EventName != AgentHooks.UsageEvent || message.Agent != "opencode" || DailyUsage.ReadStep(message.Payload) is not { } step)
        {
            return;
        }

        lock (_gate)
        {
            _openCode.Add(DateTime.Now, step.Cost, step.Tokens, step.Cached);
        }

        SaveOpenCode(force: false);
        Changed?.Invoke();
    }

    private void SaveOpenCode(bool force)
    {
        string text;
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _openCodeSaved < SaveEvery)
            {
                return;
            }

            _openCodeSaved = DateTime.UtcNow;
            text = _openCode.Save();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_openCodeFile)!);
            File.WriteAllText(_openCodeFile, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Today's figures are a nicety; they are counted again from the next step.
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
