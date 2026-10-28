//! What outside callers may ask the notch to do, through `notch://` links, `notchctl` and the
//! webhook. Ported from `src/Notch.Core/Automation`.

pub mod command;
pub mod forwarded_launch;
pub mod parser;
pub mod pipe;
pub mod webhook;

pub use command::{NotchCommand, TimerAction, Toast};
pub use pipe::CommandResult;
pub use webhook::{WebhookAction, WebhookRefusal};
