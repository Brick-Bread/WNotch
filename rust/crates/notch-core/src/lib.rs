//! Notch's logic that does not touch Windows: settings, commands, activities, agents, widgets,
//! the shelf, clipboard history and updates. Ported from `src/Notch.Core`, minus the plugin
//! system, which stays in .NET in the plugin host.
//!
//! Nothing here starts threads or timers. Where the C# code waits on a `TimeProvider`, the Rust
//! code takes the current time as an argument and says when it next needs to be called; the app
//! drives it.

pub mod activities;
pub mod agents;
pub mod animation;
pub mod automation;
pub mod clipboard;
pub mod hud;
pub mod json;
pub mod media;
pub mod settings;
pub mod shelf;
pub mod shell;
pub mod terminal;
pub mod updates;
pub mod widgets;

#[cfg(test)]
mod fixtures;
