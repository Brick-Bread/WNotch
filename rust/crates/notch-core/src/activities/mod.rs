//! What the pill shows. Ported from `src/Notch.Core/Activities`.

pub mod activity;
pub mod elapsed_clock;
pub mod glow;
pub mod manager;

pub use activity::{Activity, ActivityTier};
pub use glow::{Glow, GlowColor, GlowPattern};
pub use manager::ActivityManager;
