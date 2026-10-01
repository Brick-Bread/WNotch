using System.Runtime.InteropServices;
using Notch.Platform.Interop;

namespace Notch.Platform.Display;

/// <summary>
/// Reports when another app's window comes to the front, so the notch can react at once
/// instead of at its next periodic check. Create and dispose it on a thread with a message
/// loop; that is also the thread the callback runs on. Only one can exist at a time.
/// </summary>
public sealed unsafe class ForegroundWatcher : IDisposable
{
    private static Action? _changed;
    private nint _hook;

    /// <param name="changed">Must not throw: it is called straight from Windows.</param>
    public ForegroundWatcher(Action changed)
    {
        _changed = changed;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            0,
            &OnEvent,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
            _changed = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time) =>
        _changed?.Invoke();
}
