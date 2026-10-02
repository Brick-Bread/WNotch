//! Finds the program a terminal profile names and works out how to start it.

use std::ffi::OsStr;
use std::path::{Path, PathBuf};

use notch_core::agents::AgentKind;
use notch_core::presets::TerminalProfile;

/// What Windows uses when `PATHEXT` is not set.
const DEFAULT_PATH_EXT: &str = ".COM;.EXE;.BAT;.CMD";

/// The command word that stands for the platform's shell.
const SHELL_WORD: &str = "shell";

/// A program and its arguments, ready for the pseudo terminal.
#[derive(Debug, PartialEq, Eq)]
pub struct Launch {
    pub program: String,
    pub args: Vec<String>,
}

/// The extensions `PATHEXT` lists, or the Windows defaults.
pub fn parse_path_ext(path_ext: Option<&str>) -> Vec<String> {
    let text = path_ext.filter(|t| !t.trim().is_empty()).unwrap_or(DEFAULT_PATH_EXT);
    text.split(';').map(str::trim).filter(|e| !e.is_empty()).map(str::to_owned).collect()
}

/// Resolves a command name through `path_var` the way a shell would. With `extensions` (Windows)
/// a bare name is only tried with each of them, as npm also drops an extensionless shell script
/// next to its `.cmd` shim, which Windows cannot execute. Without any, the name is used as is.
/// `None` when it is not installed.
pub fn find_on_path(
    command: &str,
    path_var: Option<&OsStr>,
    extensions: &[String],
    exists: impl Fn(&Path) -> bool,
) -> Option<PathBuf> {
    let lowered = command.to_lowercase();
    let has_extension = extensions.iter().any(|e| lowered.ends_with(&e.to_lowercase()));
    let names: Vec<String> = if extensions.is_empty() || has_extension {
        vec![command.to_owned()]
    } else {
        extensions.iter().map(|e| format!("{command}{e}")).collect()
    };

    if Path::new(command).is_absolute() {
        return names.iter().map(PathBuf::from).find(|p| exists(p));
    }

    std::env::split_paths(path_var?)
        .flat_map(|directory| names.iter().map(move |name| directory.join(name)))
        .find(|candidate| exists(candidate))
}

/// Like [`find_on_path`] with this process's environment and the file system.
pub fn find_installed(command: &str) -> Option<PathBuf> {
    let extensions = if cfg!(windows) {
        parse_path_ext(std::env::var("PATHEXT").ok().as_deref())
    } else {
        Vec::new()
    };
    find_on_path(command, std::env::var_os("PATH").as_deref(), &extensions, is_runnable)
}

fn is_runnable(path: &Path) -> bool {
    if !path.is_file() {
        return false;
    }

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        path.metadata().is_ok_and(|m| m.permissions().mode() & 0o111 != 0)
    }
    #[cfg(not(unix))]
    true
}

/// The command a profile means, with the `shell` word replaced by the platform's shell.
pub fn command_word(profile: &TerminalProfile) -> String {
    if !profile.command.eq_ignore_ascii_case(SHELL_WORD) {
        return profile.command.clone();
    }

    if cfg!(windows) {
        "powershell".to_owned()
    } else {
        std::env::var("SHELL").ok().filter(|s| !s.is_empty()).unwrap_or_else(|| "bash".to_owned())
    }
}

fn is_powershell(word: &str) -> bool {
    word.eq_ignore_ascii_case("powershell")
}

/// The executable behind a profile, or `None` when it is not installed. `powershell` prefers
/// the modern `pwsh` when there is one.
pub fn resolve(profile: &TerminalProfile) -> Option<PathBuf> {
    let word = command_word(profile);
    if is_powershell(&word) {
        return find_installed("pwsh").or_else(|| find_installed(&word));
    }

    let direct = Path::new(&word);
    if direct.is_file() {
        return Some(direct.to_path_buf());
    }
    find_installed(&word)
}

/// The arguments a profile starts with before Notch adds its hook ones: a plain PowerShell gets
/// its banner switched off, anything else runs as written.
pub fn base_arguments(profile: &TerminalProfile) -> Vec<String> {
    let plain_powershell = profile.agent == AgentKind::None
        && profile.arguments.is_empty()
        && is_powershell(&command_word(profile));
    if plain_powershell {
        vec!["-NoLogo".to_owned()]
    } else {
        profile.arguments.clone()
    }
}

fn is_batch(path: &Path) -> bool {
    path.extension()
        .and_then(OsStr::to_str)
        .is_some_and(|e| e.eq_ignore_ascii_case("cmd") || e.eq_ignore_ascii_case("bat"))
}

/// How to start `executable`. Batch files cannot be started directly on Windows, so they run
/// through `cmd.exe`.
///
/// Limitation: the pseudo terminal library quotes arguments with the C runtime rules, which
/// `cmd.exe /s` cannot undo, so a batch file whose path or arguments contain spaces fails.
/// The npm shims the agent CLIs install live in a folder without spaces in the usual case.
pub fn launch_command(executable: &Path, args: &[String]) -> Launch {
    let path = executable.to_string_lossy().into_owned();
    if cfg!(windows) && is_batch(executable) {
        let mut full = vec!["/d".to_owned(), "/s".to_owned(), "/c".to_owned(), path];
        full.extend_from_slice(args);
        Launch { program: "cmd.exe".to_owned(), args: full }
    } else {
        Launch { program: path, args: args.to_vec() }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn exts() -> Vec<String> {
        parse_path_ext(Some(".EXE;.CMD"))
    }

    fn path_of(dirs: &[&str]) -> std::ffi::OsString {
        std::env::join_paths(dirs).expect("valid path list")
    }

    #[test]
    fn path_ext_falls_back_to_defaults() {
        assert_eq!(parse_path_ext(None), [".COM", ".EXE", ".BAT", ".CMD"]);
        assert_eq!(parse_path_ext(Some("  ")), [".COM", ".EXE", ".BAT", ".CMD"]);
        assert_eq!(parse_path_ext(Some(".A; ;.B;")), [".A", ".B"]);
    }

    #[test]
    fn bare_names_need_an_extension() {
        let path = path_of(&["bin1", "bin2"]);
        let present =
            |p: &Path| p == Path::new("bin2").join("claude.CMD") || p == Path::new("bin1").join("claude");
        let found = find_on_path("claude", Some(&path), &[".CMD".to_owned()], present);
        assert_eq!(found, Some(Path::new("bin2").join("claude.CMD")));
    }

    #[test]
    fn names_with_an_extension_are_tried_as_written() {
        let path = path_of(&["bin"]);
        let present = |p: &Path| p == Path::new("bin").join("tool.exe");
        let found = find_on_path("tool.exe", Some(&path), &exts(), present);
        assert_eq!(found, Some(Path::new("bin").join("tool.exe")));
    }

    #[test]
    fn without_extensions_the_name_is_used_as_is() {
        let path = path_of(&["a", "b"]);
        let present = |p: &Path| p == Path::new("b").join("bash");
        assert_eq!(find_on_path("bash", Some(&path), &[], present), Some(Path::new("b").join("bash")));
    }

    #[test]
    fn missing_commands_and_paths_give_none() {
        assert_eq!(find_on_path("nope", Some(&path_of(&["a"])), &exts(), |_| false), None);
        assert_eq!(find_on_path("nope", None, &exts(), |_| true), None);
    }

    #[test]
    fn rooted_commands_skip_the_search() {
        let root = std::env::current_dir().expect("cwd").join("tool.exe");
        let command = root.to_string_lossy().into_owned();
        let found = find_on_path(&command, None, &exts(), |p| p == root);
        assert_eq!(found, Some(root));
    }

    #[test]
    fn batch_files_run_through_cmd() {
        let launch = launch_command(Path::new(r"C:\npm\claude.cmd"), &["--x".to_owned()]);
        if cfg!(windows) {
            assert_eq!(launch.program, "cmd.exe");
            assert_eq!(launch.args, ["/d", "/s", "/c", r"C:\npm\claude.cmd", "--x"]);
        } else {
            assert_eq!(launch.program, r"C:\npm\claude.cmd");
        }
    }

    #[test]
    fn executables_start_directly() {
        let launch = launch_command(Path::new("/usr/bin/bash"), &["-l".to_owned()]);
        assert_eq!(launch, Launch { program: "/usr/bin/bash".to_owned(), args: vec!["-l".to_owned()] });
    }
}
