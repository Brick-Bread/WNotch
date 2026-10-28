//! Win32 styling, placement and focus for windows. Ported from
//! `src/Notch.Platform/Display/OverlayWindow.cs`, `Display/ForegroundWatcher.cs` and
//! `Agents/WindowFocus.cs`.

use std::sync::Mutex;

use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT, TRUE};
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::Accessibility::{HWINEVENTHOOK, SetWinEventHook, UnhookWinEvent};
use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, EVENT_SYSTEM_FOREGROUND, EnumWindows, GW_OWNER, GWL_EXSTYLE, GetCursorPos, GetForegroundWindow,
    GetWindow, GetWindowLongPtrW, GetWindowRect, GetWindowTextLengthW, GetWindowThreadProcessId, HWND_TOPMOST,
    IsIconic, IsWindowVisible, SW_RESTORE, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SetForegroundWindow,
    SetWindowLongPtrW, SetWindowPos, ShowWindow, WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW,
};
use windows::core::BOOL;

use crate::display::PixelRect;

/// A window handle as plain data, so it can cross threads and API boundaries.
pub type WindowHandle = isize;

fn hwnd(handle: WindowHandle) -> HWND {
    HWND(handle as *mut _)
}

fn ex_style(window: WindowHandle) -> isize {
    // SAFETY: reading a style of any window handle is harmless; an invalid one reads 0.
    unsafe { GetWindowLongPtrW(hwnd(window), GWL_EXSTYLE) }
}

fn set_ex_style(window: WindowHandle, style: isize) {
    // SAFETY: as above; the style is one this module computed from the window's own.
    unsafe {
        SetWindowLongPtrW(hwnd(window), GWL_EXSTYLE, style);
    }
}

/// Keeps the window out of Alt+Tab and stops clicks on it from taking focus.
pub fn apply_overlay_styles(window: WindowHandle) {
    set_ex_style(window, ex_style(window) | (WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0) as isize);
}

/// Turn off while something inside the notch needs keyboard input.
pub fn set_no_activate(window: WindowHandle, no_activate: bool) {
    let style = ex_style(window);
    let flag = WS_EX_NOACTIVATE.0 as isize;
    set_ex_style(window, if no_activate { style | flag } else { style & !flag });
}

pub fn bounds(window: WindowHandle) -> PixelRect {
    let mut rect = RECT::default();
    // SAFETY: `rect` is a valid out-pointer.
    match unsafe { GetWindowRect(hwnd(window), &mut rect) } {
        Ok(()) => PixelRect::new(rect.left, rect.top, rect.right, rect.bottom),
        Err(_) => PixelRect::default(),
    }
}

pub fn set_bounds(window: WindowHandle, bounds: PixelRect) {
    // SAFETY: plain values; a bad handle makes the call fail harmlessly.
    unsafe {
        let _ = SetWindowPos(
            hwnd(window),
            Some(HWND_TOPMOST),
            bounds.left,
            bounds.top,
            bounds.width(),
            bounds.height(),
            SWP_NOACTIVATE,
        );
    }
}

/// Other topmost windows can end up above the notch; this puts it back on top.
pub fn bring_to_top(window: WindowHandle) {
    // SAFETY: as above.
    unsafe {
        let _ = SetWindowPos(hwnd(window), Some(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

/// The window that has the keyboard, to hand it back to later with [`set_foreground`].
pub fn foreground() -> WindowHandle {
    // SAFETY: no arguments.
    unsafe { GetForegroundWindow() }.0 as WindowHandle
}

pub fn set_foreground(window: WindowHandle) {
    // SAFETY: a bad handle makes the call fail harmlessly.
    unsafe {
        let _ = SetForegroundWindow(hwnd(window));
    }
}

/// The pointer's position in physical screen pixels, or `None` when Windows will not say (e.g. on the lock screen).
pub fn cursor_position() -> Option<(i32, i32)> {
    let mut point = POINT::default();
    // SAFETY: `point` is a valid out-pointer.
    unsafe { GetCursorPos(&mut point) }.ok().map(|()| (point.x, point.y))
}

/// Whether the left, right or middle mouse button is down, wherever the pointer is.
pub fn is_any_mouse_button_down() -> bool {
    // SAFETY: plain virtual-key codes.
    [0x01, 0x02, 0x04].into_iter().any(|key| unsafe { GetAsyncKeyState(key) } as u16 & 0x8000 != 0)
}

/// The process that owns the window in front right now, or 0.
pub fn foreground_process_id() -> u32 {
    let window = foreground();
    if window == 0 {
        return 0;
    }
    let mut pid = 0u32;
    // SAFETY: `pid` is a valid out-pointer.
    unsafe { GetWindowThreadProcessId(hwnd(window), Some(&mut pid)) };
    pid
}

/// Raises the first window owned by one of `candidate_pids`, preferring the earlier ones in the
/// list. False when none of them has a window.
pub fn focus_process_window(candidate_pids: &[u32]) -> bool {
    let mut found: Vec<(WindowHandle, u32)> = Vec::new();
    let mut search = (candidate_pids, &mut found);
    // SAFETY: the callback only runs during this call and gets a pointer to `search`.
    unsafe {
        let _ = EnumWindows(Some(collect), LPARAM(&raw mut search as isize));
    }

    for pid in candidate_pids {
        if let Some(&(window, _)) = found.iter().find(|(_, p)| p == pid) {
            raise(window);
            return true;
        }
    }
    false
}

unsafe extern "system" fn collect(window: HWND, state: LPARAM) -> BOOL {
    // SAFETY: `state` is the tuple `focus_process_window` passed, alive for the enumeration.
    let (pids, found) = unsafe { &mut *(state.0 as *mut (&[u32], &mut Vec<(WindowHandle, u32)>)) };
    // Visible, unowned, titled, and not a tool window: a window a person would call the app's.
    // SAFETY: plain queries on a window handle Windows gave this callback.
    let suitable = unsafe {
        IsWindowVisible(window).as_bool()
            && GetWindow(window, GW_OWNER).map_or(true, |owner| owner.0.is_null())
            && GetWindowTextLengthW(window) != 0
    } && ex_style(window.0 as WindowHandle) & WS_EX_TOOLWINDOW.0 as isize == 0;
    if suitable {
        let mut pid = 0u32;
        // SAFETY: `pid` is a valid out-pointer.
        unsafe { GetWindowThreadProcessId(window, Some(&mut pid)) };
        if pids.contains(&pid) {
            found.push((window.0 as WindowHandle, pid));
        }
    }
    TRUE
}

fn raise(window: WindowHandle) {
    let window = hwnd(window);
    // SAFETY: plain calls on a window handle; the thread input is detached again before returning.
    unsafe {
        if IsIconic(window).as_bool() {
            let _ = ShowWindow(window, SW_RESTORE);
        }

        // Windows only lets the program that has the keyboard take the foreground; joining its
        // input queue for a moment gets around that.
        let foreground = GetForegroundWindow();
        let foreground_thread = if foreground.0.is_null() { 0 } else { GetWindowThreadProcessId(foreground, None) };
        let this_thread = GetCurrentThreadId();
        let attached = foreground_thread != 0
            && foreground_thread != this_thread
            && AttachThreadInput(this_thread, foreground_thread, true).as_bool();

        let _ = BringWindowToTop(window);
        let _ = SetForegroundWindow(window);

        if attached {
            let _ = AttachThreadInput(this_thread, foreground_thread, false);
        }
    }
}

static FOREGROUND_CHANGED: Mutex<Option<Box<dyn Fn() + Send>>> = Mutex::new(None);

/// Reports when another app's window comes to the front, so the notch can react at once instead
/// of at its next periodic check. Create and drop it on a thread with a message loop; that is
/// also the thread the callback runs on. Only one can exist at a time.
pub struct ForegroundWatcher {
    hook: HWINEVENTHOOK,
}

impl ForegroundWatcher {
    /// `changed` must not panic: it is called straight from Windows.
    pub fn new(changed: impl Fn() + Send + 'static) -> Self {
        *FOREGROUND_CHANGED.lock().unwrap_or_else(|p| p.into_inner()) = Some(Box::new(changed));
        // SAFETY: an out-of-context hook with a plain callback; it is removed on drop.
        let hook = unsafe {
            SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                None,
                Some(on_event),
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
            )
        };
        Self { hook }
    }
}

impl Drop for ForegroundWatcher {
    fn drop(&mut self) {
        if !self.hook.is_invalid() {
            // SAFETY: the hook came from SetWinEventHook and is removed once.
            unsafe {
                let _ = UnhookWinEvent(self.hook);
            }
        }
        *FOREGROUND_CHANGED.lock().unwrap_or_else(|p| p.into_inner()) = None;
    }
}

unsafe extern "system" fn on_event(_: HWINEVENTHOOK, _: u32, _: HWND, _: i32, _: i32, _: u32, _: u32) {
    if let Some(changed) = FOREGROUND_CHANGED.lock().unwrap_or_else(|p| p.into_inner()).as_ref() {
        changed();
    }
}
