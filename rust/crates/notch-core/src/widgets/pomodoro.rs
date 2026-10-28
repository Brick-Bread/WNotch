//! Focus sessions and breaks. Ported from `src/Notch.Core/Widgets/PomodoroCycle.cs`.

use std::time::Duration;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PomodoroDurations {
    pub focus: Duration,
    pub short_break: Duration,
    pub long_break: Duration,
    pub focuses_per_long_break: u32,
}

impl PomodoroDurations {
    pub const CLASSIC: Self = Self {
        focus: Duration::from_secs(25 * 60),
        short_break: Duration::from_secs(5 * 60),
        long_break: Duration::from_secs(15 * 60),
        focuses_per_long_break: 4,
    };
}
