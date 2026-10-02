using System.Runtime.InteropServices;

namespace Notch.Platform.Agents;

/// <summary>Brings the window an agent runs in to the front.</summary>
public static unsafe class WindowFocus
{
    private sealed class Search
    {
        public required HashSet<int> Pids { get; init; }

        public List<(nint Window, int Pid)> Found { get; } = [];
    }

    /// <summary>
    /// Raises the first window owned by one of <paramref name="candidatePids"/>, preferring the
    /// earlier ones in the list. Returns false when none of them has a window.
    /// </summary>
    public static bool Focus(IReadOnlyList<int> candidatePids)
    {
        var search = new Search { Pids = [.. candidatePids] };
        GCHandle handle = GCHandle.Alloc(search);
        try
        {
            ProcessNative.EnumWindows(&Collect, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        foreach (int pid in candidatePids)
        {
            nint window = search.Found.FirstOrDefault(f => f.Pid == pid).Window;
            if (window != 0)
            {
                Raise(window);
                return true;
            }
        }

        return false;
    }

    /// <summary>The process that owns the window in front right now, or 0.</summary>
    public static int ForegroundProcessId()
    {
        nint window = ProcessNative.GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        ProcessNative.GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint window, nint state)
    {
        var search = (Search?)GCHandle.FromIntPtr(state).Target;
        if (search is null || !ProcessNative.IsWindowVisible(window) || ProcessNative.GetWindow(window, ProcessNative.GW_OWNER) != 0
            || ProcessNative.GetWindowTextLength(window) == 0
            || (ProcessNative.GetWindowLongPtr(window, ProcessNative.GWL_EXSTYLE) & ProcessNative.WS_EX_TOOLWINDOW) != 0)
        {
            return 1;
        }

        ProcessNative.GetWindowThreadProcessId(window, out uint pid);
        if (search.Pids.Contains((int)pid))
        {
            search.Found.Add((window, (int)pid));
        }

        return 1;
    }

    private static void Raise(nint window)
    {
        if (ProcessNative.IsIconic(window))
        {
            ProcessNative.ShowWindow(window, ProcessNative.SW_RESTORE);
        }

        // Windows only lets the program that has the keyboard take the foreground; joining its input queue for a moment gets around that.
        nint foreground = ProcessNative.GetForegroundWindow();
        uint foregroundThread = foreground == 0 ? 0 : ProcessNative.GetWindowThreadProcessId(foreground, out _);
        uint thisThread = ProcessNative.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread
            && ProcessNative.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            ProcessNative.BringWindowToTop(window);
            ProcessNative.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                ProcessNative.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }
}
