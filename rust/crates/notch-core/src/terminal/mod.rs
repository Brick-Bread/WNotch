//! Terminal sessions: presets and the commands they run. Ported from `src/Notch.Core/Terminal`.

/// The terminal buttons a new install starts with, one per line as the settings keep them.
pub const DEFAULT_PRESETS: [&str; 4] =
    ["Claude = claude", "Codex = codex", "opencode = opencode", "Shell = powershell"];
