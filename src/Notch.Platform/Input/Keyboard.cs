using Notch.Core.Shell;
using Notch.Platform.Interop;

namespace Notch.Platform.Input;

/// <summary>The keyboard as a whole, whichever app is being typed into.</summary>
public static class Keyboard
{
    /// <summary>The window message a registered hotkey arrives as; its wParam is the id it was registered with.</summary>
    public const int HotkeyMessage = 0x0312;

    /// <summary>
    /// Whether Caps Lock is switched on. Windows keeps this per thread and refreshes it as the
    /// thread handles messages, so ask on the UI thread.
    /// </summary>
    public static bool IsCapsLockOn => (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;

    /// <summary>
    /// Has Windows send <see cref="HotkeyMessage"/> to the window whenever the keys are pressed.
    /// False when Windows or another app already uses that combination.
    /// </summary>
    public static bool RegisterHotkey(nint hwnd, int id, Hotkey hotkey) =>
        NativeMethods.RegisterHotKey(hwnd, id, (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)hotkey.VirtualKey);

    public static void UnregisterHotkey(nint hwnd, int id) => NativeMethods.UnregisterHotKey(hwnd, id);
}
