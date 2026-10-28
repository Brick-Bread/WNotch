//! Ported from `src/Notch.Core/Shell/IslandPlacement.cs`.

use crate::settings::{NotchPosition, NotchStyle};

/// Where the island is on its display and how it meets the screen edge, worked out from the
/// user's position and style settings. Sizes are in DIPs unless a parameter says otherwise.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct IslandPlacement {
    pub position: NotchPosition,
    pub style: NotchStyle,
    /// Height of the taskbar on the display; only matters in the taskbar position.
    pub taskbar_height: f64,
}

impl IslandPlacement {
    /// Used when the taskbar's height cannot be told, e.g. while it hides itself.
    pub const DEFAULT_TASKBAR_HEIGHT: f64 = 48.0;

    /// Height of the idle pill, which a floating island centres in the taskbar.
    pub const PILL_HEIGHT: f64 = 32.0;

    /// How far a floating island keeps from the top edge.
    pub const TOP_GAP: f64 = 8.0;

    /// How far the island keeps from the left edge in the taskbar position.
    pub const SIDE_INSET: f64 = 12.0;

    const SMALLEST_BOTTOM_GAP: f64 = 4.0;

    /// A placement with [`Self::DEFAULT_TASKBAR_HEIGHT`].
    pub fn new(position: NotchPosition, style: NotchStyle) -> Self {
        Self::with_taskbar(position, style, Self::DEFAULT_TASKBAR_HEIGHT)
    }

    pub fn with_taskbar(position: NotchPosition, style: NotchStyle, taskbar_height: f64) -> Self {
        Self { position, style, taskbar_height }
    }

    /// The island sits on the bottom edge and opens upwards, rather than hanging from the top.
    pub fn at_bottom(&self) -> bool {
        self.position == NotchPosition::TaskbarLeft
    }

    /// The island is rounded all the way around and keeps clear of the edge.
    pub fn floating(&self) -> bool {
        self.style == NotchStyle::Island
    }

    /// Space between the screen edge and the island: none for a notch, enough to centre the pill in the taskbar there.
    pub fn edge_gap(&self) -> f64 {
        if !self.floating() {
            0.0
        } else if self.at_bottom() {
            Self::SMALLEST_BOTTOM_GAP.max((self.taskbar_height - Self::PILL_HEIGHT) / 2.0)
        } else {
            Self::TOP_GAP
        }
    }

    /// True when a screen point is on the island or in the gap between it and its edge, so that
    /// pushing the pointer against the edge still reaches a floating island.
    ///
    /// - `display_left`..`display_bottom`: the display's bounds, in physical pixels.
    /// - `scale`: physical pixels per DIP.
    /// - `width`, `height`: the island's current size.
    /// - `x`, `y`: the point, in physical pixels.
    /// - `margin`: extra room around the island so a pointer resting on its outline does not
    ///   waver; C# defaults it to 0.
    #[allow(clippy::too_many_arguments)]
    pub fn contains(
        &self,
        display_left: i32,
        display_top: i32,
        display_right: i32,
        display_bottom: i32,
        scale: f64,
        width: f64,
        height: f64,
        x: i32,
        y: i32,
        margin: f64,
    ) -> bool {
        let (x, y) = (f64::from(x), f64::from(y));
        let reach = (self.edge_gap() + height + margin) * scale;
        if self.at_bottom() {
            let left = f64::from(display_left) + (Self::SIDE_INSET - margin) * scale;
            let right = f64::from(display_left) + (Self::SIDE_INSET + width + margin) * scale;
            let bottom = f64::from(display_bottom);
            return x >= left && x < right && y < bottom && y >= bottom - reach;
        }

        let centre = (f64::from(display_left) + f64::from(display_right)) / 2.0;
        let half = (width / 2.0 + margin) * scale;
        let top = f64::from(display_top);
        x >= centre - half && x < centre + half && y >= top && y < top + reach
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // A 2560-wide display at 150%, to the right of another one; the idle pill is 180 x 32 DIPs.
    #[test]
    fn island_hit_test_follows_the_display() {
        let notch = IslandPlacement::new(NotchPosition::TopCenter, NotchStyle::Notch);
        for (x, y, expected) in [
            (3200, 0, true),
            (3066, 47, true),
            (3334, 47, true),
            (3064, 10, false),
            (3335, 10, false),
            (3200, 48, false),
            (3200, -1, false),
        ] {
            assert_eq!(notch.contains(1920, 0, 4480, 1440, 1.5, 180.0, 32.0, x, y, 0.0), expected, "{x}, {y}");
        }
    }

    #[test]
    fn island_hit_test_margin_reaches_past_the_edge() {
        let notch = IslandPlacement::new(NotchPosition::TopCenter, NotchStyle::Notch);

        assert!(!notch.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, 871, 33, 0.0));
        assert!(notch.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, 869, 33, 2.0));
    }

    #[test]
    fn a_floating_island_keeps_clear_of_the_edge_but_is_reached_through_the_gap() {
        let island = IslandPlacement::new(NotchPosition::TopCenter, NotchStyle::Island);

        assert_eq!(island.edge_gap(), IslandPlacement::TOP_GAP);
        assert_eq!(IslandPlacement::new(NotchPosition::TopCenter, NotchStyle::Notch).edge_gap(), 0.0);

        // The very top row of pixels, the island itself, and just below it.
        assert!(island.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, 960, 0, 0.0));
        assert!(island.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, 960, 39, 0.0));
        assert!(!island.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, 960, 40, 0.0));
    }

    // A 1920 x 1080 display at 100% with the usual 48-high taskbar; the idle pill is 180 x 32.
    #[test]
    fn in_the_taskbar_the_island_sits_bottom_left() {
        for (style, x, y, expected) in [
            (NotchStyle::Island, 12, 1079, true),
            (NotchStyle::Island, 191, 1040, true),
            (NotchStyle::Island, 192, 1050, false),
            (NotchStyle::Island, 11, 1050, false),
            (NotchStyle::Island, 100, 1039, false),
            (NotchStyle::Notch, 100, 1048, true),
            (NotchStyle::Notch, 100, 1047, false),
            (NotchStyle::Notch, 960, 0, false),
        ] {
            let placement = IslandPlacement::with_taskbar(NotchPosition::TaskbarLeft, style, 48.0);
            assert_eq!(
                placement.contains(0, 0, 1920, 1080, 1.0, 180.0, 32.0, x, y, 0.0),
                expected,
                "{style:?} {x}, {y}"
            );
        }
    }

    #[test]
    fn a_floating_island_is_centred_in_the_taskbar() {
        for (taskbar_height, gap) in [(48.0, 8.0), (72.0, 20.0), (30.0, 4.0)] {
            let placement =
                IslandPlacement::with_taskbar(NotchPosition::TaskbarLeft, NotchStyle::Island, taskbar_height);
            assert_eq!(placement.edge_gap(), gap);
        }
    }
}
