//! Finding out that another app is full screen on the notch's display, so the notch can get out
//! of the way of a game or a video.
//!
//! Windows walks the window stack like the C# `FullscreenDetector`. Linux asks an X server for
//! `_NET_WM_STATE_FULLSCREEN` windows; under Wayland there is no way for one app to see another's
//! windows, so nothing is ever reported there.

use notch_core::shell::{is_covered, StackedWindow};

/// A display's bounds in physical pixels, virtual-desktop coordinates.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Bounds {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl Bounds {
    pub fn contains(&self, other: &Bounds) -> bool {
        self.left <= other.left && self.top <= other.top && self.right >= other.right && self.bottom >= other.bottom
    }

    /// True when the two share any area; touching edges do not count.
    pub fn intersects(&self, other: &Bounds) -> bool {
        self.left < other.right && other.left < self.right && self.top < other.bottom && other.top < self.bottom
    }
}

/// True when a fullscreen app (game, video, F11 browser) is what shows on `display`: either it has
/// the keyboard, or it is the uppermost application window there, as when the user is working on
/// another display while a game runs on this one. `own_window` is the notch's, which never counts.
pub fn is_fullscreen_app_on(display: Bounds, own_window: Option<isize>) -> bool {
    platform::is_fullscreen_app_on(display, own_window)
}

/// Calls `changed` whenever another app's window comes to the front, so the notch reacts at once
/// instead of at its next periodic check. Where the system has no such signal, nothing happens
/// and the periodic check alone applies. Install it from the thread that runs the event loop.
pub fn watch_foreground(changed: impl Fn() + Send + Sync + 'static) {
    platform::watch_foreground(Box::new(changed));
}

#[cfg(windows)]
mod platform {
    use std::sync::OnceLock;

    use windows::core::BOOL;
    use windows::Win32::Foundation::{HWND, RECT};
    use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
    use windows::Win32::Graphics::Gdi::{GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST};
    use windows::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
    use windows::Win32::UI::WindowsAndMessaging::{
        GetClassNameW, GetForegroundWindow, GetShellWindow, GetTopWindow, GetWindow, GetWindowLongPtrW, GetWindowRect,
        IsIconic, IsWindowVisible, EVENT_SYSTEM_FOREGROUND, GWL_EXSTYLE, GWL_STYLE, GW_HWNDNEXT,
        WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS, WS_CAPTION, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        WS_EX_TOPMOST, WS_EX_TRANSPARENT,
    };

    use super::{is_covered, Bounds, StackedWindow};

    /// Walking the window stack races with windows being created and destroyed; this bounds it.
    const MAX_WINDOWS: usize = 2000;

    const SHELL_CLASSES: [&str; 4] = ["Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow"];

    static CHANGED: OnceLock<Box<dyn Fn() + Send + Sync>> = OnceLock::new();

    pub fn is_fullscreen_app_on(display: Bounds, own_window: Option<isize>) -> bool {
        let own = own_window.unwrap_or(0);
        // SAFETY: plain queries about windows; a stale handle only makes them fail.
        unsafe {
            let foreground = GetForegroundWindow();
            if !foreground.0.is_null() && foreground.0 as isize != own && covers_display(foreground, display) {
                return true;
            }
            is_covered(windows_on(display, own))
        }
    }

    pub fn watch_foreground(changed: Box<dyn Fn() + Send + Sync>) {
        if CHANGED.set(changed).is_err() {
            return;
        }
        // SAFETY: the callback is a plain function and the hook lives as long as the process.
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
        if hook.is_invalid() {
            log::warn!("could not watch for foreground changes; falling back to polling");
        }
    }

    unsafe extern "system" fn on_event(_: HWINEVENTHOOK, _: u32, _: HWND, _: i32, _: i32, _: u32, _: u32) {
        if let Some(changed) = CHANGED.get() {
            changed();
        }
    }

    /// The application windows that overlap the display, uppermost first.
    unsafe fn windows_on(display: Bounds, own: isize) -> Vec<StackedWindow> {
        let mut found = Vec::new();
        let mut current = GetTopWindow(None).ok();
        let mut seen = 0;
        while let Some(hwnd) = current {
            if seen >= MAX_WINDOWS {
                break;
            }
            seen += 1;
            current = GetWindow(hwnd, GW_HWNDNEXT).ok();

            if hwnd.0 as isize == own {
                continue;
            }
            let Some(ex_style) = application_window_style(hwnd) else { continue };
            match window_rect(hwnd) {
                Some(rect) if rect.intersects(&display) => {}
                _ => continue,
            }
            found.push(StackedWindow {
                covers_display: covers_display(hwnd, display),
                is_topmost: ex_style & WS_EX_TOPMOST.0 != 0,
            });
        }
        found
    }

    /// The window's extended style when it is a window the user sees as an app's; `None` for what
    /// they do not: hidden, minimised and cloaked windows (other virtual desktops, suspended
    /// store apps), and the click-through overlays that graphics drivers and chat apps keep
    /// stretched over the whole screen.
    unsafe fn application_window_style(hwnd: HWND) -> Option<u32> {
        if !IsWindowVisible(hwnd).as_bool() || IsIconic(hwnd).as_bool() {
            return None;
        }
        let ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        if ex_style & (WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0 | WS_EX_TRANSPARENT.0) != 0 {
            return None;
        }
        let mut cloaked = 0u32;
        let read = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, (&raw mut cloaked).cast(), size_of::<u32>() as u32);
        (read.is_err() || cloaked == 0).then_some(ex_style)
    }

    unsafe fn covers_display(hwnd: HWND, display: Bounds) -> bool {
        if hwnd == GetShellWindow() {
            return false;
        }
        if monitor_bounds(hwnd) != Some(display) {
            return false;
        }
        if !window_rect(hwnd).is_some_and(|rect| rect.contains(&display)) {
            return false;
        }
        // A maximized window also covers the monitor when the taskbar auto-hides, but it keeps its caption.
        let style = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
        if style & WS_CAPTION.0 == WS_CAPTION.0 {
            return false;
        }
        let mut buffer = [0u16; 256];
        let length = usize::try_from(GetClassNameW(hwnd, &mut buffer)).unwrap_or(0);
        let class = String::from_utf16_lossy(&buffer[..length.min(buffer.len())]);
        !SHELL_CLASSES.contains(&class.as_str())
    }

    unsafe fn window_rect(hwnd: HWND) -> Option<Bounds> {
        let mut rect = RECT::default();
        GetWindowRect(hwnd, &mut rect).ok()?;
        Some(Bounds { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom })
    }

    /// The bounds of the display the window is mostly on.
    unsafe fn monitor_bounds(hwnd: HWND) -> Option<Bounds> {
        let monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        let known: BOOL = GetMonitorInfoW(monitor, &mut info);
        let rect = info.rcMonitor;
        known.as_bool().then_some(Bounds { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom })
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::{Atom, AtomEnum, ConnectionExt, Window};
    use x11rb::rust_connection::RustConnection;

    use super::{is_covered, Bounds, StackedWindow};

    /// Asks the X server for the stacked client windows (bottom to top) and whether each is full screen.
    pub fn is_fullscreen_app_on(display: Bounds, own_window: Option<isize>) -> bool {
        let Ok((connection, screen)) = RustConnection::connect(None) else {
            // Wayland, or no display at all: other apps' windows cannot be seen.
            return false;
        };
        let root = connection.setup().roots[screen].root;
        let own = own_window.and_then(|w| u32::try_from(w).ok());
        stacked(&connection, root, display, own).is_some_and(is_covered)
    }

    pub fn watch_foreground(_: Box<dyn Fn() + Send + Sync>) {
        log::debug!("no foreground signal on this system; fullscreen apps are found by polling");
    }

    fn atom(connection: &RustConnection, name: &str) -> Option<Atom> {
        Some(connection.intern_atom(false, name.as_bytes()).ok()?.reply().ok()?.atom)
    }

    fn stacked(connection: &RustConnection, root: Window, display: Bounds, own: Option<u32>) -> Option<Vec<StackedWindow>> {
        let list = atom(connection, "_NET_CLIENT_LIST_STACKING")?;
        let state = atom(connection, "_NET_WM_STATE")?;
        let fullscreen = atom(connection, "_NET_WM_STATE_FULLSCREEN")?;
        let above = atom(connection, "_NET_WM_STATE_ABOVE")?;

        let windows = connection
            .get_property(false, root, list, AtomEnum::WINDOW, 0, 4096)
            .ok()?
            .reply()
            .ok()?
            .value32()?
            .collect::<Vec<u32>>();

        let mut found = Vec::new();
        // The list runs from the bottom of the stack to the top.
        for window in windows.into_iter().rev() {
            if Some(window) == own {
                continue;
            }
            let states = connection
                .get_property(false, window, state, AtomEnum::ATOM, 0, 64)
                .ok()
                .and_then(|cookie| cookie.reply().ok())
                .and_then(|reply| reply.value32().map(Iterator::collect::<Vec<u32>>))
                .unwrap_or_default();
            let Some(rect) = frame(connection, root, window) else { continue };
            if !rect.intersects(&display) {
                continue;
            }
            found.push(StackedWindow {
                covers_display: states.contains(&fullscreen) && rect.contains(&display),
                is_topmost: states.contains(&above),
            });
        }
        Some(found)
    }

    /// The window's place on the screen.
    fn frame(connection: &RustConnection, root: Window, window: Window) -> Option<Bounds> {
        let geometry = connection.get_geometry(window).ok()?.reply().ok()?;
        let origin = connection.translate_coordinates(window, root, 0, 0).ok()?.reply().ok()?;
        let left = i32::from(origin.dst_x);
        let top = i32::from(origin.dst_y);
        Some(Bounds {
            left,
            top,
            right: left + i32::from(geometry.width),
            bottom: top + i32::from(geometry.height),
        })
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
mod platform {
    use super::Bounds;

    pub fn is_fullscreen_app_on(_: Bounds, _: Option<isize>) -> bool {
        false
    }

    pub fn watch_foreground(_: Box<dyn Fn() + Send + Sync>) {}
}

#[cfg(test)]
mod tests {
    use super::*;

    const DISPLAY: Bounds = Bounds { left: 0, top: 0, right: 1920, bottom: 1080 };

    #[test]
    fn a_window_that_fills_the_display_contains_it() {
        assert!(DISPLAY.contains(&DISPLAY));
        assert!(Bounds { left: -1, top: -1, right: 1921, bottom: 1081 }.contains(&DISPLAY));
        assert!(!Bounds { left: 0, top: 0, right: 1919, bottom: 1080 }.contains(&DISPLAY));
    }

    #[test]
    fn touching_edges_do_not_intersect() {
        let next = Bounds { left: 1920, top: 0, right: 3840, bottom: 1080 };
        assert!(!DISPLAY.intersects(&next));
        assert!(DISPLAY.intersects(&Bounds { left: 1919, top: 0, right: 3840, bottom: 1080 }));
    }
}
