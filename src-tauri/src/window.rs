//! The one window: its size for each state and its place on the top edge of a display.
//!
//! TODO(hide_in_fullscreen): the setting is stored but not acted on. There is no portable API
//! to learn that another app is full screen; it needs per-platform code (`SHQueryUserState` on
//! Windows, the compositor on Linux).

use std::sync::{Arc, Mutex, MutexGuard};

use tauri::{AppHandle, Emitter, Manager, Monitor, PhysicalPosition, PhysicalSize, WebviewWindow};

/// Label of the window declared in `tauri.conf.json`.
const MAIN_WINDOW: &str = "main";

/// Logical size of the collapsed pill.
const COLLAPSED: (f64, f64) = (180.0, 32.0);

/// Logical size while an activity shows.
const COMPACT: (f64, f64) = (360.0, 32.0);

/// Logical size of the open notch.
const EXPANDED: (f64, f64) = (600.0, 372.0);

/// The logical window size for a state. Opening wins over an activity showing.
pub fn window_size(expanded: bool, compact: bool) -> (f64, f64) {
    if expanded {
        EXPANDED
    } else if compact {
        COMPACT
    } else {
        COLLAPSED
    }
}

/// The left edge that centres a window of `width` on a display starting at `monitor_x`.
pub fn centered_x(monitor_x: i32, monitor_width: u32, width: u32) -> i32 {
    let spare = i64::from(monitor_width) - i64::from(width);
    monitor_x + i32::try_from(spare / 2).unwrap_or(0)
}

#[derive(Default)]
struct Layout {
    expanded: bool,
    compact: bool,
    display_index: Option<i32>,
}

/// Sizes and places the window. Cheap to clone; all clones share one state.
#[derive(Clone)]
pub struct WindowController {
    app: AppHandle,
    layout: Arc<Mutex<Layout>>,
}

impl WindowController {
    pub fn new(app: AppHandle, display_index: Option<i32>) -> Self {
        let layout = Layout { display_index, ..Layout::default() };
        Self { app, layout: Arc::new(Mutex::new(layout)) }
    }

    fn layout(&self) -> MutexGuard<'_, Layout> {
        self.layout.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn window(&self) -> Option<WebviewWindow> {
        self.app.get_webview_window(MAIN_WINDOW)
    }

    /// Places the window and shows it. It is created hidden so it never appears in the wrong place.
    pub fn show(&self) {
        self.apply();
        if let Some(window) = self.window() {
            if let Err(e) = window.show() {
                log::warn!("could not show the window: {e}");
            }
        }
    }

    /// Grows or shrinks the window because the UI asked. The UI already knows, so no event.
    pub fn set_expanded(&self, expanded: bool) {
        self.layout().expanded = expanded;
        self.apply();
    }

    /// Opens or closes on the backend's own initiative and tells the UI with `expanded`.
    pub fn request_expanded(&self, expanded: bool) {
        self.set_expanded(expanded);
        if expanded {
            if let Some(Err(e)) = self.window().map(|w| w.set_focus()) {
                log::debug!("could not focus the window: {e}");
            }
        }
        if let Err(e) = self.app.emit("expanded", serde_json::json!({ "expanded": expanded })) {
            log::warn!("could not emit expanded: {e}");
        }
    }

    /// Opens a closed notch and closes an open one (the global hotkey).
    pub fn toggle(&self) {
        let expanded = self.layout().expanded;
        self.request_expanded(!expanded);
    }

    /// Widens the pill while an activity shows.
    pub fn set_compact(&self, compact: bool) {
        let changed = std::mem::replace(&mut self.layout().compact, compact) != compact;
        if changed {
            self.apply();
        }
    }

    /// Moves the notch to another display.
    pub fn set_display(&self, display_index: Option<i32>) {
        let changed = std::mem::replace(&mut self.layout().display_index, display_index) != display_index;
        if changed {
            self.apply();
        }
    }

    /// Brings the window in line with the layout: size, place on the display and whether it can take focus.
    fn apply(&self) {
        let Some(window) = self.window() else { return };
        let (expanded, compact, display_index) = {
            let layout = self.layout();
            (layout.expanded, layout.compact, layout.display_index)
        };

        // Only an open notch may take keyboard focus; a closed one must never steal it.
        if let Err(e) = window.set_focusable(expanded) {
            log::debug!("could not change focusability: {e}");
        }

        let (width, height) = window_size(expanded, compact);
        let monitor = self.pick_monitor(display_index);
        let scale = monitor.as_ref().map_or(1.0, Monitor::scale_factor);
        let size = PhysicalSize::new((width * scale).round() as u32, (height * scale).round() as u32);
        if let Err(e) = window.set_size(size) {
            log::warn!("could not resize the window: {e}");
        }

        if let Some(monitor) = monitor {
            let origin = monitor.position();
            let x = centered_x(origin.x, monitor.size().width, size.width);
            if let Err(e) = window.set_position(PhysicalPosition::new(x, origin.y)) {
                log::warn!("could not move the window: {e}");
            }
        }
    }

    /// The display at `display_index`, else the primary one, else any.
    fn pick_monitor(&self, display_index: Option<i32>) -> Option<Monitor> {
        let monitors = self.app.available_monitors().unwrap_or_default();
        let chosen = display_index.and_then(|i| usize::try_from(i).ok()).and_then(|i| monitors.get(i).cloned());
        chosen
            .or_else(|| self.app.primary_monitor().ok().flatten())
            .or_else(|| monitors.into_iter().next())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sizes_follow_the_state() {
        assert_eq!(window_size(false, false), (180.0, 32.0));
        assert_eq!(window_size(false, true), (360.0, 32.0));
        assert_eq!(window_size(true, false), (600.0, 372.0));
        assert_eq!(window_size(true, true), (600.0, 372.0));
    }

    #[test]
    fn centres_on_a_display() {
        assert_eq!(centered_x(0, 1920, 600), 660);
        assert_eq!(centered_x(1920, 2560, 360), 1920 + 1100);
        assert_eq!(centered_x(-1920, 1920, 180), -1920 + 870);
    }

    #[test]
    fn a_window_wider_than_the_display_hangs_off_both_sides() {
        assert_eq!(centered_x(0, 400, 600), -100);
    }
}
