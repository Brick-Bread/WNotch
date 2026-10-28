//! Rules for the notch window: hotkeys, placement, modes, fullscreen, notifications, palette search. Ported from `src/Notch.Core/Shell`.

pub mod fullscreen;
pub mod hotkey;
pub mod island;
pub mod mode;
pub mod notification_mirror;
pub mod opening;
pub mod palette_search;
pub mod placement;

pub use fullscreen::StackedWindow;
pub use hotkey::{Hotkey, HotkeyModifiers};
pub use mode::NotchMode;
pub use notification_mirror::{NotificationDiff, NotificationInfo};
pub use placement::IslandPlacement;
