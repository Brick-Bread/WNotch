namespace Notch.Platform.Agents;

/// <summary>
/// Reads the title of the console a program runs in: what a terminal tab is named after. Notch has no
/// console of its own, so it borrows the program's for a moment, as a console tool started from it would.
/// </summary>
public static unsafe class ConsoleTitle
{
    private static readonly object Gate = new();

    /// <summary>The title of <paramref name="pid"/>'s console, or null when it has none or it cannot be reached.</summary>
    public static string? Of(int pid)
    {
        // One console at a time per process, so one read at a time.
        lock (Gate)
        {
            if (!ProcessNative.AttachConsole((uint)pid))
            {
                return null;
            }

            // While attached, Ctrl+C pressed in that console would reach Notch too; ignore it for the moment.
            ProcessNative.SetConsoleCtrlHandler(0, true);
            try
            {
                char* buffer = stackalloc char[1024];
                uint length = ProcessNative.GetConsoleTitle(buffer, 1024);
                return length == 0 ? null : new string(buffer, 0, (int)Math.Min(length, 1023u));
            }
            finally
            {
                ProcessNative.FreeConsole();
                ProcessNative.SetConsoleCtrlHandler(0, false);
            }
        }
    }
}
