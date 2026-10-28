//! The island's size as it animates, and where the window that holds it goes. Ported from
//! `src/Notch.App/Shell/NotchAnimator.cs` and the sizing in `src/Notch.App/Shell/NotchWindow.xaml.cs`.
//!
//! The page animates the island with these same springs; the app steps its own copy towards the
//! same targets, so it knows the island's size at any moment (to tell whether the pointer is on
//! it) without the page reporting every frame.

use crate::animation::Spring;

const SIZE_STIFFNESS: f64 = 380.0;
const SIZE_DAMPING: f64 = 26.0;
const RADIUS_STIFFNESS: f64 = 300.0;
const RADIUS_DAMPING: f64 = 30.0;

/// The island's size and corner radius, in DIPs.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Shape {
    pub width: f64,
    pub height: f64,
    pub radius: f64,
}

impl Shape {
    pub const fn new(width: f64, height: f64, radius: f64) -> Self {
        Self { width, height, radius }
    }
}

/// Three springs that move the island's width, height and corner radius.
#[derive(Debug, Clone)]
pub struct IslandAnimator {
    width: Spring,
    height: Spring,
    radius: Spring,
}

impl IslandAnimator {
    pub fn new(start: Shape) -> Self {
        Self {
            width: Spring::with(start.width, SIZE_STIFFNESS, SIZE_DAMPING),
            height: Spring::with(start.height, SIZE_STIFFNESS, SIZE_DAMPING),
            radius: Spring::with(start.radius, RADIUS_STIFFNESS, RADIUS_DAMPING),
        }
    }

    pub fn animate_to(&mut self, target: Shape) {
        self.width.target = target.width;
        self.height.target = target.height;
        self.radius.target = target.radius;
    }

    pub fn target(&self) -> Shape {
        Shape::new(self.width.target, self.height.target, self.radius.target)
    }

    /// Moves the springs on by `seconds`.
    pub fn step(&mut self, seconds: f64) {
        for spring in [&mut self.width, &mut self.height, &mut self.radius] {
            if !spring.is_settled() {
                spring.step(seconds);
            }
        }
    }

    pub fn shape(&self) -> Shape {
        Shape::new(self.width.value(), self.height.value(), self.radius.value())
    }

    pub fn is_running(&self) -> bool {
        !(self.width.is_settled() && self.height.is_settled() && self.radius.is_settled())
    }
}

/// The window's size in DIPs. Large fits the biggest expanded tab; small fits the pill plus room
/// for its glow. Both are the same width: a resize that moved the left edge would show the
/// previous frame in the wrong place until the next one is drawn, leaving nothing under the pointer.
pub const LARGE_WINDOW: (f64, f64) = (980.0, 700.0);
pub const SMALL_WINDOW: (f64, f64) = (980.0, 96.0);

/// A rectangle in physical screen pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ScreenRect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl ScreenRect {
    pub const fn new(left: i32, top: i32, right: i32, bottom: i32) -> Self {
        Self { left, top, right, bottom }
    }

    pub const fn width(&self) -> i32 {
        self.right - self.left
    }
}

/// Where the window goes on a display with these bounds and scale: centred on the top edge, or
/// in the bottom-left corner when the island lives in the taskbar. At the bottom the window
/// would have to grow upwards, moving its top edge, and the frame drawn before the resize would
/// show in the wrong place, so it stays large there.
pub fn window_bounds(display: ScreenRect, scale: f64, at_bottom: bool, large: bool) -> ScreenRect {
    let (width, height) = if large || at_bottom { LARGE_WINDOW } else { SMALL_WINDOW };
    let width = (width * scale).round_ties_even() as i32;
    let height = (height * scale).round_ties_even() as i32;
    if at_bottom {
        ScreenRect::new(display.left, display.bottom - height, display.left + width, display.bottom)
    } else {
        let left = display.left + (display.width() - width) / 2;
        ScreenRect::new(left, display.top, left + width, display.top + height)
    }
}

/// The heights the island rests at, by where it sits. At the top of the screen the resting notch
/// is a slim bar and a live activity a low pill, so they cover as little as they can of what is
/// there, such as a browser's tabs. In the taskbar they keep the height the taskbar is laid out for.
pub fn idle_height(at_bottom: bool) -> f64 {
    if at_bottom { super::IslandPlacement::PILL_HEIGHT } else { 10.0 }
}

pub fn compact_height(at_bottom: bool) -> f64 {
    if at_bottom { super::IslandPlacement::PILL_HEIGHT } else { 26.0 }
}

/// A HUD (volume, Caps Lock, a toast) is a little taller than a live activity at the top; in the
/// taskbar it must not jump out of the height the taskbar is laid out for either.
pub fn peek_height(at_bottom: bool) -> f64 {
    if at_bottom { super::IslandPlacement::PILL_HEIGHT } else { 38.0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_window_is_centred_at_the_top_and_cornered_at_the_bottom() {
        let display = ScreenRect::new(0, 0, 2560, 1440);
        assert_eq!(window_bounds(display, 1.5, false, false), ScreenRect::new(545, 0, 2015, 144));
        assert_eq!(window_bounds(display, 1.5, false, true), ScreenRect::new(545, 0, 2015, 1050));
        // At the bottom it stays large.
        assert_eq!(window_bounds(display, 1.0, true, false), ScreenRect::new(0, 740, 980, 1440));
    }

    #[test]
    fn a_second_display_keeps_its_offset() {
        let display = ScreenRect::new(-1920, 0, 0, 1080);
        assert_eq!(window_bounds(display, 1.0, false, false), ScreenRect::new(-1450, 0, -470, 96));
    }

    #[test]
    fn the_animator_reaches_its_target_with_a_little_overshoot() {
        let mut animator = IslandAnimator::new(Shape::new(180.0, 10.0, 5.0));
        animator.animate_to(Shape::new(640.0, 400.0, 32.0));
        let mut highest: f64 = 0.0;
        for _ in 0..120 {
            animator.step(1.0 / 60.0);
            highest = highest.max(animator.shape().width);
        }
        assert!(!animator.is_running());
        assert!((animator.shape().width - 640.0).abs() < 0.1);
        assert!(highest > 640.0, "springs overshoot a little");
    }
}
