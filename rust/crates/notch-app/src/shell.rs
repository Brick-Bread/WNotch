//! The notch window: where it sits, when it opens, what lets clicks through, and when it hides.
//! Ported from `src/Notch.App/Shell/NotchWindow.xaml.cs`.
//!
//! The page draws and animates the island. This side polls the pointer, decides when the notch
//! opens and closes (`notch_core::shell::opening`), keeps the window placed, on top and out of
//! the way of fullscreen apps, and lets clicks through everywhere but the island.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use notch_core::activities::ActivityManager;
use notch_core::settings::{AppSettings, NotchPosition, NotchStyle};
use notch_core::shell::island::{self, IslandAnimator, ScreenRect, Shape};
use notch_core::shell::opening::{Change, Holds, Opening, Pointer};
use notch_core::shell::{IslandPlacement, NotchMode, mode};
use notch_platform::display::{self, DisplayInfo, PixelRect};
use notch_platform::{fullscreen, registry, window};
use tauri::{Emitter, WebviewWindow};

use crate::view::{ActivityView, ShellView, mode_name};

// How far outside the island, in DIPs, the pointer still counts as on it.
const HOVER_MARGIN: f64 = 2.0;
const HOUSEKEEPING: Duration = Duration::from_secs(1);
pub const POLL: Duration = Duration::from_millis(25);

/// Choices for this run that leave the saved settings alone (development flags).
#[derive(Debug, Clone, Default)]
pub struct Overrides {
    pub display: Option<usize>,
    pub style: Option<NotchStyle>,
    pub position: Option<NotchPosition>,
    pub pin_open: bool,
}

/// Activities, shared with the sources that publish them; `changed` tells the shell to redraw.
#[derive(Clone)]
pub struct Activities {
    pub manager: Arc<Mutex<ActivityManager>>,
    pub changed: Arc<AtomicBool>,
}

impl Activities {
    pub fn new() -> Self {
        Self { manager: Arc::new(Mutex::new(ActivityManager::new())), changed: Arc::new(AtomicBool::new(true)) }
    }

    pub fn with<R>(&self, change: impl FnOnce(&mut ActivityManager) -> R) -> R {
        let result = change(&mut self.manager.lock().unwrap_or_else(|p| p.into_inner()));
        self.changed.store(true, Ordering::Relaxed);
        result
    }
}

pub struct Shell {
    window: WebviewWindow,
    hwnd: window::WindowHandle,
    settings: AppSettings,
    overrides: Overrides,
    activities: Activities,
    opening: Opening,
    animator: IslandAnimator,
    holds: Holds,
    displays: Vec<DisplayInfo>,
    display: Option<DisplayInfo>,
    placement: IslandPlacement,
    large: bool,
    hidden_for_fullscreen: bool,
    click_through: Option<bool>,
    pointer: Pointer,
    last_step: Instant,
    next_housekeeping: Instant,
    last_view: Option<ShellView>,
}

impl Shell {
    pub fn new(window: WebviewWindow, settings: AppSettings, overrides: Overrides, activities: Activities) -> Self {
        let hwnd = window.hwnd().map(|h| h.0 as window::WindowHandle).unwrap_or_default();
        window::apply_overlay_styles(hwnd);
        let placement = IslandPlacement::new(NotchPosition::TopCenter, NotchStyle::Notch);
        let idle = Shape::new(180.0, island::idle_height(false), island::idle_height(false) / 2.0);
        let mut shell = Self {
            window,
            hwnd,
            settings,
            overrides,
            activities,
            opening: Opening::new(),
            animator: IslandAnimator::new(idle),
            holds: Holds::default(),
            displays: Vec::new(),
            display: None,
            placement,
            large: false,
            hidden_for_fullscreen: false,
            click_through: None,
            pointer: Pointer::default(),
            last_step: Instant::now(),
            next_housekeeping: Instant::now(),
            last_view: None,
        };
        shell.reposition();
        if shell.overrides.pin_open {
            shell.opening.pin_open(shell.pointer);
        }
        shell.refresh();
        shell
    }

    /// One poll: the pointer, the springs, pending opens and closes, expired activities, and
    /// housekeeping once a second.
    pub fn tick(&mut self, now: Instant) {
        let seconds = (now - self.last_step).as_secs_f64();
        self.last_step = now;
        self.animator.step(seconds);

        if now >= self.next_housekeeping {
            self.next_housekeeping = now + HOUSEKEEPING;
            self.housekeeping();
        }
        if self.hidden_for_fullscreen {
            return;
        }

        self.pointer = Pointer { over: self.pointer_over_island(), button_down: window::is_any_mouse_button_down() };
        let change = self.opening.poll(self.pointer, self.holds, self.settings.expand_on_hover, now);
        self.apply(change);

        let expired = self.activities.manager.lock().unwrap_or_else(|p| p.into_inner()).tick(now);
        if expired || self.activities.changed.swap(false, Ordering::Relaxed) {
            self.refresh();
        }

        // Transparent pixels let clicks through; only the island takes them.
        self.set_click_through(!self.pointer.over);
    }

    fn apply(&mut self, change: Change) {
        if change.hand_back_keyboard {
            // The window that had the keyboard before the notch took it gets it back (step 4).
        }
        if change.expanded_changed {
            self.refresh();
        }
    }

    pub fn island_clicked(&mut self) {
        let change = self.opening.island_clicked(self.pointer);
        self.apply(change);
    }

    pub fn set_holds(&mut self, holds: Holds) {
        self.holds = holds;
    }

    /// The page moved the island towards a new shape; the springs here follow it there.
    pub fn shape_target(&mut self, target: Shape) {
        self.animator.animate_to(target);
    }

    /// Tells the page everything again, as when it has just loaded.
    pub fn resend(&mut self) {
        self.last_view = None;
        self.refresh();
    }

    fn set_click_through(&mut self, through: bool) {
        if self.click_through != Some(through) {
            self.click_through = Some(through);
            let _ = self.window.set_ignore_cursor_events(through);
        }
    }

    /// Whether the pointer is on the island, judged by where both are on screen.
    fn pointer_over_island(&self) -> bool {
        let (Some(display), Some((x, y))) = (self.display.as_ref(), window::cursor_position()) else {
            return false;
        };
        let shape = self.animator.shape();
        let b = display.bounds;
        self.placement.contains(
            b.left,
            b.top,
            b.right,
            b.bottom,
            display.scale,
            shape.width.max(0.0),
            shape.height.max(island::idle_height(self.placement.at_bottom())),
            x,
            y,
            HOVER_MARGIN,
        )
    }

    fn housekeeping(&mut self) {
        // Displays plugged in or out, or a scale changed: the notch is placed again.
        let displays = display::all();
        if displays != self.displays {
            self.displays = displays;
            self.reposition();
        }

        // Not while the user is typing into the notch: then it is the foreground window, by their
        // choice. Nor in the middle of a drag out of it, which may well end on a fullscreen app.
        let fullscreen = self.settings.hide_in_fullscreen
            && !self.holds.keyboard_in_use
            && !self.holds.drag_out
            && self.display.as_ref().is_some_and(|d| fullscreen::is_fullscreen_app_on(d, self.hwnd));
        self.set_hidden_for_fullscreen(fullscreen);
        if !fullscreen {
            window::bring_to_top(self.hwnd);
        }

        // Catches a shrink that was put off because the pointer was resting on the pill.
        if !self.animator.is_running() {
            self.shrink_when_clear();
        }
    }

    /// Takes the whole window off the screen while a fullscreen app shows on this display, so
    /// nothing of the notch, not even a HUD or its glow, is drawn over a game.
    fn set_hidden_for_fullscreen(&mut self, hidden: bool) {
        if self.hidden_for_fullscreen == hidden {
            return;
        }
        self.hidden_for_fullscreen = hidden;
        if hidden {
            let change = self.opening.close(self.pointer);
            self.apply(change);
            let _ = self.window.hide();
        } else {
            // The window never takes the keyboard when it shows, so coming back does not either.
            let _ = self.window.show();
            self.reposition();
        }
    }

    /// Returns the window to its small size once the notch is closed and the pointer is off it.
    fn shrink_when_clear(&mut self) {
        if !self.opening.is_expanded() && self.large && !self.pointer_over_island() {
            self.set_large(false);
        }
    }

    /// Every repaint of a transparent window copies the whole window, so it stays only as big as
    /// the pill needs and grows just before the notch expands.
    fn set_large(&mut self, large: bool) {
        if self.large != large {
            self.large = large;
            self.reposition();
        }
    }

    /// Pins the window to the chosen display, in physical pixels: centred on the top edge, or in
    /// the bottom-left corner when the island lives in the taskbar.
    fn reposition(&mut self) {
        if self.displays.is_empty() {
            self.displays = display::all();
        }
        // The saved display may have been unplugged since; fall back to the primary one.
        let index = self.overrides.display.or(self.settings.display_index.and_then(|i| usize::try_from(i).ok()));
        let Some(display) = index.and_then(|i| self.displays.get(i)).cloned().or_else(display::primary) else {
            return;
        };

        // An auto-hiding taskbar takes no room from the work area; assume the usual height then.
        let taskbar = f64::from(display.bounds.bottom - display.work_area.bottom) / display.scale;
        let placement = IslandPlacement::with_taskbar(
            self.overrides.position.unwrap_or(self.settings.position),
            self.overrides.style.unwrap_or(self.settings.style),
            if taskbar > 0.0 { taskbar } else { IslandPlacement::DEFAULT_TASKBAR_HEIGHT },
        );
        let placement_changed = placement != self.placement;
        self.placement = placement;

        let b = display.bounds;
        let bounds = island::window_bounds(
            ScreenRect::new(b.left, b.top, b.right, b.bottom),
            display.scale,
            placement.at_bottom(),
            self.large,
        );
        let target = PixelRect::new(bounds.left, bounds.top, bounds.right, bounds.bottom);
        if window::bounds(self.hwnd) != target {
            window::set_bounds(self.hwnd, target);
        }
        self.display = Some(display);

        // The resting and compact heights depend on where the notch sits.
        if placement_changed {
            self.refresh();
        }
    }

    fn refresh(&mut self) {
        let activities = self.activities.manager.lock().unwrap_or_else(|p| p.into_inner()).snapshot();
        let mode = mode::resolve(self.opening.is_expanded(), &activities);
        if mode == NotchMode::Expanded {
            self.set_large(true);
        }

        let at_bottom = self.placement.at_bottom();
        let view = ShellView {
            mode: mode_name(mode),
            expanded: self.opening.is_expanded(),
            at_bottom,
            floating: self.placement.floating(),
            edge_gap: self.placement.edge_gap(),
            side_inset: if at_bottom { IslandPlacement::SIDE_INSET } else { 0.0 },
            idle_height: island::idle_height(at_bottom),
            compact_height: island::compact_height(at_bottom),
            peek_height: island::peek_height(at_bottom),
            light: self.settings.theme.is_light(registry::apps_use_light_theme()),
            glow_effects: self.settings.glow_effects,
            activities: activities.iter().map(ActivityView::from).collect(),
        };
        if self.last_view.as_ref() != Some(&view) {
            let _ = self.window.emit("notch-state", &view);
            self.last_view = Some(view);
        }
    }
}
