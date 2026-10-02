namespace Notch.Core.Agents;

/// <summary>One running program, as the process scan sees it.</summary>
/// <param name="CommandLine">Only read for programs that might be an agent; null otherwise.</param>
public sealed record ProcessSnapshot(
    int Pid,
    int ParentPid,
    string Name,
    string? CommandLine = null,
    string? ExecutablePath = null,
    DateTime? StartedAt = null);

/// <summary>An agent found among the running programs.</summary>
/// <param name="Key">Stable while the agent runs; a reused process id gets a different one.</param>
/// <param name="HostPid">The terminal or editor the agent runs in, when it could be found.</param>
public sealed record DetectedAgent(string Key, int Pid, AgentDefinition Definition, DateTime? StartedAt, int? HostPid, string? HostName);

/// <summary>Finds coding agents among the running programs. Pure logic: the scan itself is in the platform layer.</summary>
public static class AgentDetector
{
    private const int MaxDepth = 40;

    /// <summary>Programs whose window an agent's terminal lives in, with the name to show for them.</summary>
    private static readonly Dictionary<string, string> Hosts = new()
    {
        ["windowsterminal"] = "Windows Terminal",
        ["wt"] = "Windows Terminal",
        ["code"] = "VS Code",
        ["code - insiders"] = "VS Code",
        ["cursor"] = "Cursor",
        ["windsurf"] = "Windsurf",
        ["wezterm-gui"] = "WezTerm",
        ["alacritty"] = "Alacritty",
        ["kitty"] = "kitty",
        ["hyper"] = "Hyper",
        ["tabby"] = "Tabby",
        ["conhost"] = "Console",
        ["idea64"] = "IntelliJ IDEA",
        ["pycharm64"] = "PyCharm",
        ["rider64"] = "Rider",
        ["webstorm64"] = "WebStorm",
        ["devenv"] = "Visual Studio",
    };

    /// <summary>
    /// The agents running right now. A program that Notch started itself (anything below
    /// <paramref name="ownPid"/>) is left out, since Notch's own terminal already reports it; an agent
    /// that runs helper processes of its own (a node child of claude.exe) counts once.
    /// </summary>
    /// <param name="hidden">Ids of agents the user does not want listed.</param>
    public static IReadOnlyList<DetectedAgent> Detect(IReadOnlyList<ProcessSnapshot> processes, int ownPid, IReadOnlyCollection<string>? hidden = null)
    {
        var byPid = new Dictionary<int, ProcessSnapshot>();
        foreach (ProcessSnapshot process in processes)
        {
            byPid[process.Pid] = process;
        }

        var matches = new Dictionary<int, AgentDefinition>();
        foreach (ProcessSnapshot process in processes)
        {
            if (AgentCatalog.Match(process) is { } agent && hidden?.Contains(agent.Id) != true)
            {
                matches[process.Pid] = agent;
            }
        }

        var found = new List<DetectedAgent>();
        foreach ((int pid, AgentDefinition agent) in matches)
        {
            ProcessSnapshot process = byPid[pid];
            List<ProcessSnapshot> ancestors = Ancestors(process, byPid);

            if (ancestors.Any(a => a.Pid == ownPid))
            {
                continue;
            }

            // The outermost process of an agent is the agent; its helpers below it are not more agents.
            if (ancestors.Any(a => matches.TryGetValue(a.Pid, out AgentDefinition? above) && above.Id == agent.Id))
            {
                continue;
            }

            ProcessSnapshot? host = ancestors.FirstOrDefault(a => Hosts.ContainsKey(AgentCatalog.NameWithoutExtension(a.Name)));
            found.Add(new DetectedAgent(
                KeyFor(process),
                pid,
                agent,
                process.StartedAt,
                host?.Pid,
                host is null ? null : Hosts[AgentCatalog.NameWithoutExtension(host.Name)]));
        }

        return [.. found.OrderBy(a => a.StartedAt ?? DateTime.MaxValue).ThenBy(a => a.Pid)];
    }

    /// <summary>Stable for one run of a program: the process id plus its start time, since ids are reused.</summary>
    public static string KeyFor(ProcessSnapshot process) => $"pid-{process.Pid}-{process.StartedAt?.Ticks ?? 0}";

    /// <summary>The detected agent <paramref name="pid"/> runs under (or is), as when a hook program reports itself.</summary>
    public static DetectedAgent? AgentOf(int pid, IReadOnlyList<ProcessSnapshot> processes, IReadOnlyList<DetectedAgent> detected)
    {
        var byPid = new Dictionary<int, ProcessSnapshot>();
        foreach (ProcessSnapshot process in processes)
        {
            byPid[process.Pid] = process;
        }

        if (!byPid.TryGetValue(pid, out ProcessSnapshot? start))
        {
            return null;
        }

        foreach (ProcessSnapshot step in new[] { start }.Concat(Ancestors(start, byPid)))
        {
            if (detected.FirstOrDefault(d => d.Pid == step.Pid) is { } agent)
            {
                return agent;
            }
        }

        return null;
    }

    /// <summary>
    /// Processes that may own a window the agent can be reached through: the agent's parents, and
    /// the programs those started (the console host a command prompt window is made of).
    /// </summary>
    public static IReadOnlyList<int> WindowCandidates(int agentPid, IReadOnlyList<ProcessSnapshot> processes)
    {
        var byPid = new Dictionary<int, ProcessSnapshot>();
        foreach (ProcessSnapshot process in processes)
        {
            byPid[process.Pid] = process;
        }

        if (!byPid.TryGetValue(agentPid, out ProcessSnapshot? agent))
        {
            return [];
        }

        List<ProcessSnapshot> ancestors = Ancestors(agent, byPid);
        var candidates = new List<int>();

        // Known hosts first, so Windows Terminal wins over the shell inside it.
        candidates.AddRange(ancestors.Where(a => Hosts.ContainsKey(AgentCatalog.NameWithoutExtension(a.Name))).Select(a => a.Pid));
        foreach (ProcessSnapshot ancestor in ancestors)
        {
            candidates.Add(ancestor.Pid);
            candidates.AddRange(processes
                .Where(p => p.ParentPid == ancestor.Pid && AgentCatalog.NameWithoutExtension(p.Name) is "conhost" or "openconsole")
                .Select(p => p.Pid));
        }

        candidates.Add(agentPid);
        return [.. candidates.Distinct()];
    }

    private static List<ProcessSnapshot> Ancestors(ProcessSnapshot process, Dictionary<int, ProcessSnapshot> byPid)
    {
        var result = new List<ProcessSnapshot>();
        var seen = new HashSet<int> { process.Pid };
        int parent = process.ParentPid;
        while (result.Count < MaxDepth && parent > 0 && seen.Add(parent) && byPid.TryGetValue(parent, out ProcessSnapshot? next))
        {
            // A parent that started after its child is a recycled id, not the real parent.
            if (process.StartedAt is { } started && next.StartedAt is { } parentStarted && parentStarted > started)
            {
                break;
            }

            result.Add(next);
            process = next;
            parent = next.ParentPid;
        }

        return result;
    }
}
