using System.Management;
using Notch.Core.Agents;

namespace Notch.Platform.Agents;

/// <summary>
/// Lists the running programs. Names and parents come from a cheap snapshot of the whole process
/// table; the command line, path and start time are asked of Windows only for programs that could
/// be an agent, and remembered, so a scan every few seconds costs next to nothing.
/// </summary>
public sealed class ProcessScanner
{
    private readonly Dictionary<int, Detail> _details = [];

    /// <summary>The running programs, or an empty list when Windows would not say.</summary>
    public IReadOnlyList<ProcessSnapshot> Scan()
    {
        List<(int Pid, int Parent, string Name)> table = ReadTable();
        if (table.Count == 0)
        {
            return [];
        }

        // Forget programs that have gone, and ones whose id was reused by something else.
        var live = table.ToDictionary(p => p.Pid, p => p);
        foreach (int pid in _details.Keys.ToArray())
        {
            if (!live.TryGetValue(pid, out var current) || current.Parent != _details[pid].Parent || !current.Name.Equals(_details[pid].Name, StringComparison.OrdinalIgnoreCase))
            {
                _details.Remove(pid);
            }
        }

        int[] needed = [.. table
            .Where(p => !_details.ContainsKey(p.Pid) && (AgentCatalog.IsAgentProgram(p.Name) || AgentCatalog.IsRuntime(p.Name)))
            .Select(p => p.Pid)];
        if (needed.Length > 0)
        {
            Fetch(needed, live);
        }

        return [.. table.Select(p =>
        {
            _details.TryGetValue(p.Pid, out Detail? detail);
            return new ProcessSnapshot(p.Pid, p.Parent, p.Name, detail?.CommandLine, detail?.Path, detail?.StartedAt);
        })];
    }

    private void Fetch(int[] pids, Dictionary<int, (int Pid, int Parent, string Name)> live)
    {
        // Remembered as "nothing known" even when the question fails, so it is not asked every scan.
        foreach (int pid in pids)
        {
            (int _, int parent, string name) = live[pid];
            _details[pid] = new Detail(parent, name, null, null, null);
        }

        foreach (int[] batch in pids.Chunk(60))
        {
            try
            {
                string filter = string.Join(" OR ", batch.Select(pid => $"ProcessId={pid}"));
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process WHERE {filter}");
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using (item)
                    {
                        int pid = Convert.ToInt32(item["ProcessId"]);
                        if (!_details.TryGetValue(pid, out Detail? old))
                        {
                            continue;
                        }

                        DateTime? started = item["CreationDate"] is string text && text.Length > 0
                            ? ManagementDateTimeConverter.ToDateTime(text).ToUniversalTime()
                            : null;
                        _details[pid] = old with
                        {
                            CommandLine = item["CommandLine"] as string,
                            Path = item["ExecutablePath"] as string,
                            StartedAt = started,
                        };
                    }
                }
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // Without details a runtime cannot be told apart, so only programs named for an agent are found.
            }
        }
    }

    private static unsafe List<(int Pid, int Parent, string Name)> ReadTable()
    {
        var result = new List<(int, int, string)>();
        nint snapshot = ProcessNative.CreateToolhelp32Snapshot(ProcessNative.TH32CS_SNAPPROCESS, 0);
        if (snapshot is 0 or -1)
        {
            return result;
        }

        try
        {
            var entry = new ProcessNative.PROCESSENTRY32W { dwSize = (uint)sizeof(ProcessNative.PROCESSENTRY32W) };
            for (bool ok = ProcessNative.Process32First(snapshot, ref entry); ok; ok = ProcessNative.Process32Next(snapshot, ref entry))
            {
                string name = new((char*)entry.szExeFile);
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, name));
            }
        }
        finally
        {
            ProcessNative.CloseHandle(snapshot);
        }

        return result;
    }

    private sealed record Detail(int Parent, string Name, string? CommandLine, string? Path, DateTime? StartedAt);
}
