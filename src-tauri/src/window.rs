//! The one window: its size for each state and its place on a display.
//!
//! The UI draws the island, its glow and everything inside; the window is only as big as that
//! needs, since a transparent window is not click-through. The backend sizes and places it for the
//! chosen display, position (top centre, or the taskbar's far left) and style, hides it while a
//! fullscreen app is showing, and tells the UI how the island meets the screen edge (`placement`).

use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;
use std::time::Duration;

use notch_core::settings::{AppSettings, NotchPosition, NotchStyle};
use notch_core::shell::{self, IslandPlacement};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, Monitor, PhysicalPosition, PhysicalSize, WebviewWindow};

use crate::fullscreen::{self, Bounds};
use crate::options::StartupOptions;

/// Label of the window declared in `tauri.conf.json`.
const MAIN_WINDOW: &str = "main";

/// Logical size of the collapsed pill.
const COLLAPSED: (f64, f64) = (180.0, 32.0);

/// Logical size while an activity shows.
const COMPACT: (f64, f64) = (360.0, 32.0);

/// Logical size of the open notch.
const EXPANDED: (f64, f64) = (600.0, 372.0);

/// How often the fullscreen check and the stay-on-top nudge run.
const HOUSEKEEPING_INTERVAL: Duration = Duration::from_secs(1);

/// A game takes the foreground a moment before its window fills the screen.
const FULLSCREEN_RECHECK_DELAY: Duration = Duration::from_millis(400);

/// The logical size of the island for a state. Opening wins over an activity showing.
pub fn window_size(expanded: bool, compact: bool) -> (f64, f64) {
    if expanded {
        EXPANDED
    } else if compact {
        COMPACT
    } else {
        COLLAPSED
    }
}

/// How the island meets the screen edge, for the UI to lay itself out: sent with the state and as
/// the `placement` event whenever it changes.
#[derive(Clone, Copy, PartialEq, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Placement {
    pub anchor: shell::Anchor,
    pub floating: bool,
    /// Logical pixels between the screen edge and a floating island.
    pub edge_gap: f64,
    /// Logical pixels between the left screen edge and an island in a corner.
    pub side_inset: f64,
}

impl From<&IslandPlacement> for Placement {
    fn from(placement: &IslandPlacement) -> Self {
        Self {
            anchor: placement.anchor,
            floating: placement.floating,
            edge_gap: placement.edge_gap(),
            side_inset: placement.side_inset(),
        }
    }
}

/// One entry of the display list in the settings.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct DisplayEntry {
    /// The value stored in `displayIndex`.
    pub index: i32,
    pub width: u32,
    pub height: u32,
    pub primary: bool,
    /// What the settings list shows: `Display 2 (2560 × 1440, primary)`.
    pub label: String,
}

/// The wording of a display in the settings list.
pub fn display_label(index: i32, width: u32, height: u32, primary: bool) -> String {
    let suffix = if primary { ", primary" } else { "" };
    format!("Display {} ({width} \u{d7} {height}{suffix})", index + 1)
}

#[derive(Default)]
struct Layout {
    expanded: bool,
    compact: bool,
    hidden_for_fullscreen: bool,
    hide_in_fullscreen: bool,
    display_index: Option<i32>,
    style: NotchStyle,
    position: NotchPosition,
    placement: Option<Placement>,
}

/// Sizes and places the window. Cheap to clone; all clones share one state.
#[derive(Clone)]
pub struct WindowController {
    app: AppHandle,
    layout: Arc<Mutex<Layout>>,
    /// `--display=`, `--style=` and `--position=`: they beat the settings for this run.
    overrides: Arc<StartupOptions>,
}

impl WindowController {
    pub fn new(app: AppHandle, settings: &AppSettings, options: StartupOptions) -> Self {
        let controller = Self { app, layout: Arc::default(), overrides: Arc::new(options) };
        controller.apply_settings(settings);
        controller
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

    /// Starts the once-a-second check that hides the window for fullscreen apps and keeps it
    /// above other always-on-top windows, and the watcher that makes it react to the foreground
    /// changing without waiting for the next second.
    pub fn start_housekeeping(&self) {
        let controller = self.clone();
        thread::spawn(move || loop {
            thread::sleep(HOUSEKEEPING_INTERVAL);
            controller.housekeeping();
        });

        let controller = self.clone();
        fullscreen::watch_foreground(move || {
            controller.housekeeping();
            let again = controller.clone();
            thread::spawn(move || {
                thread::sleep(FULLSCREEN_RECHECK_DELAY);
                again.housekeeping();
            });
        });
    }

    /// Takes in the settings that affect the window: display, position, style and hiding.
    /// Switches given on the command line keep winning for this run.
    pub fn apply_settings(&self, settings: &AppSettings) {
        {
            let mut layout = self.layout();
            layout.display_index = self.overrides.display.or(settings.display_index);
            layout.style = self.overrides.style.unwrap_or(settings.style);
            layout.position = self.overrides.position.unwrap_or(settings.position);
            layout.hide_in_fullscreen = settings.hide_in_fullscreen;
        }
        self.apply();
        self.housekeeping();
    }

    /// Grows or shrinks the window because the UI asked. The UI already knows, so no event.
    pub fn set_expanded(&self, expanded: bool) {
        self.layout().expanded = expanded;
        self.apply();
    }

    /// True while the notch is open.
    pub fn is_expanded(&self) -> bool {
        self.layout().expanded
    }

    /// Opens or closes on the backend's own initiative and tells the UI with `expanded`.
    /// `focus` gives an opened window the keyboard (the tray's settings entry); the hotkey leaves
    /// it with the app the user is in, as the pointer does, and `hotkey` tells the UI the notch
    /// then stays open until the hotkey is pressed again or the pointer visits it and leaves.
    pub fn request_expanded(&self, expanded: bool, focus: bool, hotkey: bool) {
        self.set_expanded(expanded);
        if expanded && focus {
            if let Some(Err(e)) = self.window().map(|w| w.set_focus()) {
                log::debug!("could not focus the window: {e}");
            }
        }
        let payload = serde_json::json!({ "expanded": expanded, "hotkey": hotkey });
        if let Err(e) = self.app.emit("expanded", payload) {
            log::warn!("could not emit expanded: {e}");
        }
    }

    /// Opens a closed notch and closes an open one (the global hotkey). Nothing while the window
    /// is out of the way of a fullscreen app.
    pub fn toggle(&self) {
        let (expanded, hidden) = {
            let layout = self.layout();
            (layout.expanded, layout.hidden_for_fullscreen)
        };
        if !hidden {
            self.request_expanded(!expanded, false, true);
        }
    }

    /// Widens the pill while an activity shows.
    pub fn set_compact(&self, compact: bool) {
        let changed = std::mem::replace(&mut self.layout().compact, compact) != compact;
        if changed {
            self.apply();
        }
    }

    /// How the island meets the screen edge on the display it is on now.
    pub fn placement(&self) -> Placement {
        let (monitor, position, style) = {
            let layout = self.layout();
            (self.pick_monitor(layout.display_index), layout.position, layout.style)
        };
        Placement::from(&placement_on(monitor.as_ref(), position, style))
    }

    /// The pointer's position inside the window in logical pixels, for the UI's own hit test:
    /// the page stops getting pointer events when the window is resized under a still pointer.
    pub fn pointer_position(&self) -> Option<(f64, f64)> {
        let window = self.window()?;
        let cursor = window.cursor_position().ok()?;
        let origin = window.outer_position().ok()?;
        let scale = window.scale_factor().ok()?;
        Some(((cursor.x - f64::from(origin.x)) / scale, (cursor.y - f64::from(origin.y)) / scale))
    }

    /// The displays the settings offer, in the order `displayIndex` counts them.
    pub fn displays(&self) -> Vec<DisplayEntry> {
        let monitors = self.app.available_monitors().unwrap_or_default();
        let primary = self.app.primary_monitor().ok().flatten();
        monitors
            .iter()
            .enumerate()
            .filter_map(|(i, monitor)| {
                let index = i32::try_from(i).ok()?;
                let is_primary = primary.as_ref().is_some_and(|p| p.position() == monitor.position() && p.size() == monitor.size());
                let size = monitor.size();
                Some(DisplayEntry {
                    index,
                    width: size.width,
                    height: size.height,
                    primary: is_primary,
                    label: display_label(index, size.width, size.height, is_primary),
                })
            })
            .collect()
    }

    /// Once a second, and when the foreground changes: hides the window while a fullscreen app
    /// shows on its display, and otherwise lifts it back above other always-on-top windows.
    fn housekeeping(&self) {
        let Some(window) = self.window() else { return };
        let (display_index, hide_setting, expanded) = {
            let layout = self.layout();
            (layout.display_index, layout.hide_in_fullscreen, layout.expanded)
        };

        // Not while the user is typing into the notch: then it is the foreground window, by their choice.
        let keyboard_in_use = expanded && window.is_focused().unwrap_or(false);
        let fullscreen = hide_setting
            && !keyboard_in_use
            && self.pick_monitor(display_index).is_some_and(|monitor| {
                fullscreen::is_fullscreen_app_on(bounds_of(&monitor), native_handle(&window))
            });
        self.set_hidden_for_fullscreen(&window, fullscreen);
        if !fullscreen {
            if let Err(e) = window.set_always_on_top(true) {
                log::debug!("could not lift the window: {e}");
            }
        }
    }

    /// Takes the whole window off the screen while a fullscreen app shows on its display, so
    /// nothing of the notch, not even a HUD or its glow, is drawn over a game.
    fn set_hidden_for_fullscreen(&self, window: &WebviewWindow, hidden: bool) {
        let was_expanded = {
            let mut layout = self.layout();
            if layout.hidden_for_fullscreen == hidden {
                return;
            }
            layout.hidden_for_fullscreen = hidden;
            layout.expanded
        };
        log::debug!("hidden for fullscreen: {hidden}");
        if hidden {
            if was_expanded {
                self.request_expanded(false, false, false);
            }
            if let Err(e) = window.hide() {
                log::warn!("could not hide the window: {e}");
            }
        } else {
            self.apply();
            if let Err(e) = window.show() {
                log::warn!("could not show the window: {e}");
            }
        }
    }

    /// Brings the window in line with the layout: size, place on the display and whether it can take focus.
    fn apply(&self) {
        let Some(window) = self.window() else { return };
        let (expanded, compact, display_index, position, style) = {
            let layout = self.layout();
            (layout.expanded, layout.compact, layout.display_index, layout.position, layout.style)
        };

        // Only an open notch may take keyboard focus; a closed one must never steal it.
        if let Err(e) = window.set_focusable(expanded) {
            log::debug!("could not change focusability: {e}");
        }

        let monitor = self.pick_monitor(display_index);
        let island_placement = placement_on(monitor.as_ref(), position, style);
        self.publish_placement(Placement::from(&island_placement));

        let scale = monitor.as_ref().map_or(1.0, Monitor::scale_factor);
        let display = monitor.as_ref().map_or((1920.0, 1080.0), |m| {
            (f64::from(m.size().width) / scale, f64::from(m.size().height) / scale)
        });
        let frame = island_placement.frame(display, window_size(expanded, compact), !expanded);

        let size = PhysicalSize::new((frame.width * scale).round() as u32, (frame.height * scale).round() as u32);
        if let Err(e) = window.set_size(size) {
            log::warn!("could not resize the window: {e}");
        }
        if let Some(monitor) = monitor {
            let origin = monitor.position();
            let x = origin.x + (frame.x * scale).round() as i32;
            let y = origin.y + (frame.y * scale).round() as i32;
            if let Err(e) = window.set_position(PhysicalPosition::new(x, y)) {
                log::warn!("could not move the window: {e}");
            }
        }
    }

    fn publish_placement(&self, placement: Placement) {
        let changed = self.layout().placement.replace(placement) != Some(placement);
        if changed {
            if let Err(e) = self.app.emit("placement", placement) {
                log::warn!("could not emit placement: {e}");
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

/// The window's own native handle, which the fullscreen check must not mistake for another app's.
#[cfg(windows)]
fn native_handle(window: &WebviewWindow) -> Option<isize> {
    window.hwnd().ok().map(|hwnd| hwnd.0 as isize)
}

#[cfg(not(windows))]
fn native_handle(_: &WebviewWindow) -> Option<isize> {
    None
}

fn bounds_of(monitor: &Monitor) -> Bounds {
    let origin = monitor.position();
    let size = monitor.size();
    Bounds {
        left: origin.x,
        top: origin.y,
        right: origin.x.saturating_add(i32::try_from(size.width).unwrap_or(i32::MAX)),
        bottom: origin.y.saturating_add(i32::try_from(size.height).unwrap_or(i32::MAX)),
    }
}

/// The placement on a display: the taskbar's height comes from the space the work area leaves.
/// An auto-hiding taskbar takes none, so the usual height is assumed (on Windows; elsewhere,
/// with no panel to sit in, the taskbar position becomes the top-left corner).
fn placement_on(monitor: Option<&Monitor>, position: NotchPosition, style: NotchStyle) -> IslandPlacement {
    let measured = monitor.and_then(|monitor| {
        let work = monitor.work_area();
        let display_bottom = monitor.position().y.saturating_add(i32::try_from(monitor.size().height).ok()?);
        let work_bottom = work.position.y.saturating_add(i32::try_from(work.size.height).ok()?);
        shell::taskbar_height(display_bottom, work_bottom, monitor.scale_factor())
    });
    let has_taskbar = measured.is_some() || cfg!(not(target_os = "linux"));
    IslandPlacement::new(position, style, measured.unwrap_or(shell::DEFAULT_TASKBAR_HEIGHT), has_taskbar)
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
    fn displays_are_listed_the_way_the_settings_do() {
        assert_eq!(display_label(0, 2560, 1440, true), "Display 1 (2560 \u{d7} 1440, primary)");
        assert_eq!(display_label(1, 1920, 1080, false), "Display 2 (1920 \u{d7} 1080)");
    }

    #[test]
    fn the_placement_tells_the_ui_the_gap() {
        let island = IslandPlacement::new(NotchPosition::TaskbarLeft, NotchStyle::Island, 48.0, true);
        let placement = Placement::from(&island);
        assert_eq!(placement.anchor, shell::Anchor::BottomLeft);
        assert_eq!((placement.edge_gap, placement.side_inset), (8.0, 12.0));
        assert!(placement.floating);
    }
}
