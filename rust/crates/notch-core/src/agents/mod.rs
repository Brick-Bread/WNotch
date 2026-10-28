//! Coding agents and what they are doing. Ported from `src/Notch.Core/Agents`.

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AgentState {
    #[default]
    Idle,
    Working,
    NeedsInput,
    /// Finished a turn that the user has not looked at yet.
    Done,
}
