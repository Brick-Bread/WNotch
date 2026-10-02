using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Notch.Platform.Clipboard;

/// <summary>Tells a window when the clipboard changes, and who changed it.</summary>
public static partial class ClipboardListener
{
    /// <summary>Sent to the window registered with <see cref="Register"/> whenever the clipboard's contents change.</summary>
    public const int UpdateMessage = 0x031D;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    public static bool Register(nint hwnd) => AddClipboardFormatListener(hwnd);

    public static void Unregister(nint hwnd) => RemoveClipboardFormatListener(hwnd);

    /// <summary>Counts clipboard changes; a program that sets the clipboard itself can tell its own change by this.</summary>
    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>The name of the program that owns the clipboard's contents (without .exe), or null when it cannot be told.</summary>
    public static string? OwnerProgram()
    {
        nint owner = GetClipboardOwner();
        if (owner == 0 || GetWindowThreadProcessId(owner, out uint pid) == 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
