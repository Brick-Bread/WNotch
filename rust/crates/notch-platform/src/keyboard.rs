//! The keyboard as a whole, whichever app is being typed into. Ported from
//! `src/Notch.Platform/Input/Keyboard.cs`.

use notch_core::shell::hotkey::Hotkey;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, HOT_KEY_MODIFIERS, MOD_NOREPEAT, RegisterHotKey, UnregisterHotKey, VK_CAPITAL,
};

use crate::window::WindowHandle;

/// The window message a registered hotkey arrives as; its `wParam` is the id it was registered with.
pub const HOTKEY_MESSAGE: u32 = 0x0312;

/// Whether Caps Lock is switched on. Windows keeps this per thread and refreshes it as the thread
/// handles messages, so ask on the UI thread.
pub fn is_caps_lock_on() -> bool {
    // SAFETY: a plain virtual-key code.
    let state = unsafe { GetKeyState(i32::from(VK_CAPITAL.0)) };
    state & 1 != 0
}

/// Has Windows send [`HOTKEY_MESSAGE`] to the window whenever the keys are pressed. False when
/// Windows or another app already uses that combination.
pub fn register_hotkey(window: WindowHandle, id: i32, hotkey: Hotkey) -> bool {
    let modifiers = HOT_KEY_MODIFIERS(hotkey.modifiers.0) | MOD_NOREPEAT;
    // SAFETY: plain values; a bad handle makes the call fail.
    unsafe { RegisterHotKey(Some(HWND(window as *mut _)), id, modifiers, hotkey.virtual_key as u32) }.is_ok()
}

pub fn unregister_hotkey(window: WindowHandle, id: i32) {
    // SAFETY: as above.
    unsafe {
        let _ = UnregisterHotKey(Some(HWND(window as *mut _)), id);
    }
}
