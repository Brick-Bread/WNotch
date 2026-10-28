//! Coding agents and what they are doing. Ported from `src/Notch.Core/Agents`.
//!
//! The C# `Changed` events become return values (see [`board`] and [`status_tracker`]), and the
//! JSON Notch writes for other programs goes through [`stj`] so it matches the C# app's bytes.

pub mod activities;
pub mod app_sessions;
pub mod board;
pub mod catalog;
pub mod detector;
pub mod hook_editor;
pub mod hooks;
pub(crate) mod ignore_case;
pub mod opencode_plugin;
pub mod skill_library;
pub mod status_tracker;
mod stj;

pub use board::{AgentBoard, AgentEntry, AgentPublisher, AgentSource};
pub use catalog::AgentDefinition;
pub use detector::{DetectedAgent, ProcessSnapshot};
pub use hook_editor::HookEdit;
pub use status_tracker::{AgentKind, AgentStatusTracker};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AgentState {
    #[default]
    Idle,
    Working,
    NeedsInput,
    /// Finished a turn that the user has not looked at yet.
    Done,
}
