//! Ported from `src/Notch.Core/Terminal/TerminalProfile.cs`.

use indexmap::IndexMap;

use crate::agents::AgentKind;

/// Something the built-in terminal can launch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerminalProfile {
    pub id: String,
    pub display_name: String,
    /// Bare command name, resolved through PATH when the session starts.
    pub command: String,
    pub glyph: String,
    pub agent: AgentKind,
    /// Shown when [`Self::command`] is not installed.
    pub install_hint: String,
    /// Passed to [`Self::command`] ahead of the ones Notch adds for its hooks.
    pub arguments: Vec<String>,
    /// Variables set for this session only, such as a different config folder per account. Names
    /// are unique ignoring case, as in the C# dictionary.
    pub environment: IndexMap<String, String>,
}

impl TerminalProfile {
    fn built_in(
        id: &str,
        display_name: &str,
        command: &str,
        glyph: &str,
        agent: AgentKind,
        install_hint: &str,
    ) -> Self {
        Self {
            id: id.into(),
            display_name: display_name.into(),
            command: command.into(),
            glyph: glyph.into(),
            agent,
            install_hint: install_hint.into(),
            arguments: Vec::new(),
            environment: IndexMap::new(),
        }
    }

    pub fn claude() -> Self {
        Self::built_in(
            "claude",
            "Claude",
            "claude",
            "\u{E99A}",
            AgentKind::Claude,
            "The Claude Code CLI is not on your PATH. Install it from claude.com/claude-code, then open a new session.",
        )
    }

    pub fn codex() -> Self {
        Self::built_in(
            "codex",
            "Codex",
            "codex",
            "\u{E943}",
            AgentKind::Codex,
            "The Codex CLI is not on your PATH. Install it with: npm install -g @openai/codex",
        )
    }

    pub fn open_code() -> Self {
        Self::built_in(
            "opencode",
            "opencode",
            "opencode",
            "\u{E756}",
            AgentKind::OpenCode,
            "opencode is not on your PATH. Install it from opencode.ai, then open a new session.",
        )
    }

    pub fn shell() -> Self {
        Self::built_in(
            "shell",
            "PowerShell",
            "powershell",
            "\u{E756}",
            AgentKind::None,
            "PowerShell was not found on your PATH.",
        )
    }

    pub fn all() -> Vec<Self> {
        vec![Self::claude(), Self::codex(), Self::open_code(), Self::shell()]
    }
}
