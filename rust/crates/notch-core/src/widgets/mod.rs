//! The Home tab's widgets and what they show. Ported from `src/Notch.Core/Widgets`.

pub mod duration;
pub mod layout;
pub mod pomodoro;
pub mod timer_preset;

pub use layout::{HomeWidget, WidgetWidth};
pub use pomodoro::PomodoroDurations;
pub use timer_preset::TimerPreset;
