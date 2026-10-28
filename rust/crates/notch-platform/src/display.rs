//! Displays: their bounds, work areas and scale. Ported from `src/Notch.Platform/Display/Displays.cs`.

/// A rectangle in physical screen pixels (virtual-desktop coordinates).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct PixelRect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl PixelRect {
    pub const fn new(left: i32, top: i32, right: i32, bottom: i32) -> Self {
        Self { left, top, right, bottom }
    }

    pub const fn width(&self) -> i32 {
        self.right - self.left
    }

    pub const fn height(&self) -> i32 {
        self.bottom - self.top
    }

    pub const fn contains(&self, other: &Self) -> bool {
        self.left <= other.left && self.top <= other.top && self.right >= other.right && self.bottom >= other.bottom
    }

    /// True when the two share any area; touching edges do not count.
    pub const fn intersects(&self, other: &Self) -> bool {
        self.left < other.right && other.left < self.right && self.top < other.bottom && other.top < self.bottom
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct DisplayInfo {
    /// The monitor handle (`HMONITOR`).
    pub handle: isize,
    pub bounds: PixelRect,
    pub work_area: PixelRect,
    /// Physical pixels per device-independent pixel (1.0 at 96 DPI).
    pub scale: f64,
    pub is_primary: bool,
}

#[cfg(windows)]
pub use windows_impl::{all, primary};

#[cfg(windows)]
mod windows_impl {
    use windows::Win32::Foundation::{LPARAM, RECT, TRUE};
    use windows::Win32::Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO};
    use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
    use windows::core::BOOL;

    use super::{DisplayInfo, PixelRect};

    const MONITORINFOF_PRIMARY: u32 = 1;

    fn rect(r: RECT) -> PixelRect {
        PixelRect::new(r.left, r.top, r.right, r.bottom)
    }

    /// Every display attached, in the system's order.
    pub fn all() -> Vec<DisplayInfo> {
        let mut displays: Vec<DisplayInfo> = Vec::new();
        // SAFETY: the callback only runs during this call, and the pointer it gets is to `displays`,
        // which outlives the call.
        unsafe {
            let _ = EnumDisplayMonitors(None, None, Some(on_monitor), LPARAM(&raw mut displays as isize));
        }
        displays
    }

    /// The primary display, or the first one when none says it is primary. `None` when no display is attached.
    pub fn primary() -> Option<DisplayInfo> {
        let all = all();
        all.iter().find(|d| d.is_primary).or_else(|| all.first()).cloned()
    }

    unsafe extern "system" fn on_monitor(monitor: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> BOOL {
        // SAFETY: `data` is the `Vec` passed by `all`, alive for the whole enumeration.
        let displays = unsafe { &mut *(data.0 as *mut Vec<DisplayInfo>) };

        let mut info = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        // SAFETY: `info` is a properly sized MONITORINFO.
        if unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() {
            let (mut dpi, mut _y) = (0u32, 0u32);
            // SAFETY: both out-pointers are valid for the call.
            let scale = match unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut _y) } {
                Ok(()) => f64::from(dpi) / 96.0,
                Err(_) => 1.0,
            };
            displays.push(DisplayInfo {
                handle: monitor.0 as isize,
                bounds: rect(info.rcMonitor),
                work_area: rect(info.rcWork),
                scale,
                is_primary: info.dwFlags & MONITORINFOF_PRIMARY != 0,
            });
        }
        TRUE
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn touching_edges_do_not_intersect() {
        let a = PixelRect::new(0, 0, 100, 100);
        assert!(a.intersects(&PixelRect::new(99, 99, 200, 200)));
        assert!(!a.intersects(&PixelRect::new(100, 0, 200, 100)));
        assert!(a.contains(&PixelRect::new(10, 10, 100, 100)));
        assert!(!a.contains(&PixelRect::new(-1, 10, 50, 50)));
        assert_eq!((a.width(), a.height()), (100, 100));
    }
}
