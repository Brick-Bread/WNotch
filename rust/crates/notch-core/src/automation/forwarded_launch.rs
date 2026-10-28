//! Ported from `src/Notch.Core/Automation/ForwardedLaunch.cs`.
//!
//! What to do when a second copy of Notch starts and hands its arguments to the one already
//! running. Starting Notch by hand should bring the notch up, but a copy the installer or an
//! update starts must not: it would pop the notch open and hold it there for no reason the user
//! can see.

/// Switches that mean "started by the installer or a restart", not "the user asked to see Notch".
const QUIET: [&str; 2] = ["--updated", "--wait-for="];

/// Whether the running notch should open for a second launch with these arguments.
pub fn should_reveal(arguments: &[impl AsRef<str>]) -> bool {
    !arguments.iter().any(|argument| {
        let argument = argument.as_ref().as_bytes();
        QUIET
            .iter()
            .any(|flag| argument.len() >= flag.len() && argument[..flag.len()].eq_ignore_ascii_case(flag.as_bytes()))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starting_notch_by_hand_brings_it_up() {
        assert!(should_reveal(&[] as &[&str]));
        assert!(should_reveal(&["--settings"]));
        assert!(should_reveal(&["--tab=stats"]));
    }

    #[test]
    fn a_copy_the_installer_or_a_restart_started_stays_quiet() {
        for flag in ["--updated", "--UPDATED", "--wait-for=1234"] {
            assert!(!should_reveal(&[flag]));
            assert!(!should_reveal(&["--tab=home", flag]));
        }
    }
}
