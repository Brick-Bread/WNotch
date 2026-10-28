//! Tells a window when the clipboard changes, and who changed it. Ported from
//! `src/Notch.Platform/Clipboard/ClipboardListener.cs`.

use windows::Win32::Foundation::HWND;
use windows::Win32::System::DataExchange::{
    AddClipboardFormatListener, GetClipboardOwner, GetClipboardSequenceNumber, RemoveClipboardFormatListener,
};
use windows::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

use crate::window::WindowHandle;

/// Sent to the window registered with [`register`] whenever the clipboard's contents change.
pub const UPDATE_MESSAGE: u32 = 0x031D;

pub fn register(window: WindowHandle) -> bool {
    // SAFETY: a bad handle makes the call fail harmlessly.
    unsafe { AddClipboardFormatListener(HWND(window as *mut _)) }.is_ok()
}

pub fn unregister(window: WindowHandle) {
    // SAFETY: as above.
    unsafe {
        let _ = RemoveClipboardFormatListener(HWND(window as *mut _));
    }
}

/// Counts clipboard changes; a program that sets the clipboard itself can tell its own change by this.
pub fn sequence_number() -> u32 {
    // SAFETY: no arguments.
    unsafe { GetClipboardSequenceNumber() }
}

/// The name of the program that owns the clipboard's contents (without .exe), or `None` when it cannot be told.
pub fn owner_program() -> Option<String> {
    // SAFETY: plain queries; `pid` is a valid out-pointer.
    let pid = unsafe {
        let owner = GetClipboardOwner().ok()?;
        let mut pid = 0u32;
        if GetWindowThreadProcessId(owner, Some(&mut pid)) == 0 {
            return None;
        }
        pid
    };
    crate::process::name(pid)
}
