//! The Home tab's widgets and what they show. Ported from `src/Notch.Core/Widgets`.

pub mod countdown;
pub mod duration;
pub mod layout;
pub mod pomodoro;
pub mod stats;
pub mod timer_preset;
pub mod todo;

pub use countdown::{CountdownState, CountdownTimer};
pub use layout::{HomeWidget, WidgetOption, WidgetOptionType, WidgetType, WidgetWidth};
pub use pomodoro::{PomodoroCycle, PomodoroDurations, PomodoroPhase};
pub use stats::SystemStats;
pub use timer_preset::TimerPreset;
pub use todo::{TodoItem, TodoList};
