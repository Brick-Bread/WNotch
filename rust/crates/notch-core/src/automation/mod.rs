//! What outside callers may ask the notch to do, through `notch://` links, `notchctl` and the
//! webhook. Ported from `src/Notch.Core/Automation`.

pub mod command;
pub mod parser;

pub use command::{NotchCommand, TimerAction, Toast};
