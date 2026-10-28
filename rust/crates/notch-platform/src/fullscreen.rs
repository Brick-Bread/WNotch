//! Whether a fullscreen app is what shows on a display. Ported from
//! `src/Notch.Platform/Display/FullscreenDetector.cs`; the rule itself is
//! `notch_core::shell::fullscreen`.

use notch_core::shell::fullscreen::{StackedWindow, is_covered};
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONEAREST, MonitorFromWindow};
use windows::Win32::UI::WindowsAndMessaging::{
    GW_HWNDNEXT, GWL_EXSTYLE, GWL_STYLE, GetClassNameW, GetForegroundWindow, GetShellWindow, GetTopWindow, GetWindow,
    GetWindowLongPtrW, GetWindowRect, IsIconic, IsWindowVisible, WS_CAPTION, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    WS_EX_TOPMOST, WS_EX_TRANSPARENT,
};

use crate::display::{DisplayInfo, PixelRect};
use crate::window::WindowHandle;

// Walking the window stack races with windows being created and destroyed; this bounds it.
const MAX_WINDOWS: usize = 2000;

const SHELL_CLASSES: [&str; 4] = ["Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow"];

/// True when a fullscreen app (game, video, F11 browser) is what shows on `display`: either it has
/// the keyboard, or it is the uppermost application window there, as when the user is working on
/// another display while a game runs on this one. `own_window`, the notch, never counts.
pub fn is_fullscreen_app_on(display: &DisplayInfo, own_window: WindowHandle) -> bool {
    let own = HWND(own_window as *mut _);
    // SAFETY: no arguments.
    let foreground = unsafe { GetForegroundWindow() };
    if !foreground.0.is_null() && foreground != own && covers_display(foreground, display) {
        return true;
    }

    is_covered(windows_on(display, own))
}

/// The application windows that overlap the display, uppermost first.
fn windows_on(display: &DisplayInfo, own: HWND) -> impl Iterator<Item = StackedWindow> + '_ {
    // SAFETY: walking the window list with plain queries; a handle that has gone fails them harmlessly.
    let first = unsafe { GetTopWindow(None) }.ok();
    std::iter::successors(first, |&window| unsafe { GetWindow(window, GW_HWNDNEXT) }.ok())
        .take(MAX_WINDOWS)
        .filter(move |&window| window != own)
        .filter_map(move |window| {
            let ex_style = application_window_style(window)?;
            if !rect(window).is_some_and(|r| r.intersects(&display.bounds)) {
                return None;
            }
            Some(StackedWindow {
                covers_display: covers_display(window, display),
                is_topmost: ex_style & WS_EX_TOPMOST.0 as isize != 0,
            })
        })
}

fn rect(window: HWND) -> Option<PixelRect> {
    let mut r = RECT::default();
    // SAFETY: `r` is a valid out-pointer.
    unsafe { GetWindowRect(window, &mut r) }.ok()?;
    Some(PixelRect::new(r.left, r.top, r.right, r.bottom))
}

/// The window's extended style when it is a window the user sees as an app's: not hidden,
/// minimised or cloaked (other virtual desktops, suspended store apps), and not one of the
/// click-through overlays that graphics drivers and chat apps keep stretched over the whole screen.
fn application_window_style(window: HWND) -> Option<isize> {
    // SAFETY: plain queries on a window handle; `cloaked` is a valid out-pointer of the size given.
    unsafe {
        if !IsWindowVisible(window).as_bool() || IsIconic(window).as_bool() {
            return None;
        }

        let ex_style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if ex_style & (WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0 | WS_EX_TRANSPARENT.0) as isize != 0 {
            return None;
        }

        let mut cloaked = 0i32;
        let read = DwmGetWindowAttribute(window, DWMWA_CLOAKED, (&raw mut cloaked).cast(), size_of::<i32>() as u32);
        (read.is_err() || cloaked == 0).then_some(ex_style)
    }
}

fn covers_display(window: HWND, display: &DisplayInfo) -> bool {
    // SAFETY: plain queries on a window handle.
    unsafe {
        if window == GetShellWindow() {
            return false;
        }

        if MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST).0 as isize != display.handle {
            return false;
        }

        if !rect(window).is_some_and(|r| r.contains(&display.bounds)) {
            return false;
        }

        // A maximized window also covers the monitor when the taskbar auto-hides, but it keeps its caption.
        let style = GetWindowLongPtrW(window, GWL_STYLE);
        if style & WS_CAPTION.0 as isize == WS_CAPTION.0 as isize {
            return false;
        }

        let mut buffer = [0u16; 256];
        let length = GetClassNameW(window, &mut buffer);
        let class = String::from_utf16_lossy(&buffer[..length.max(0) as usize]);
        !SHELL_CLASSES.contains(&class.as_str())
    }
}
