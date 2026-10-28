//! Finds executables the way the shell would and builds CreateProcess command lines. Ported from
//! `src/Notch.Core/Terminal/CommandResolver.cs`.
//!
//! Paths follow Windows rules on every host (see [`super::windows_path`]); the file check and the
//! variables are passed in, so tests read the same on Linux.

use super::windows_path;
use crate::agents::ignore_case;

const DEFAULT_PATH_EXT: &str = ".COM;.EXE;.BAT;.CMD";

/// `Split(';', RemoveEmptyEntries | TrimEntries)`.
fn split_list(text: &str) -> impl Iterator<Item = &str> {
    text.split(';').map(str::trim).filter(|part| !part.is_empty())
}

/// Resolves a bare command name through PATH and PATHEXT. `None` when it is not installed.
pub fn find_on_path(
    command: &str,
    path_variable: Option<&str>,
    path_ext: Option<&str>,
    file_exists: impl Fn(&str) -> bool,
) -> Option<String> {
    let path_ext = path_ext.filter(|ext| !ext.trim().is_empty()).unwrap_or(DEFAULT_PATH_EXT);
    let extensions: Vec<&str> = split_list(path_ext).collect();

    // npm also drops an extensionless shell script next to its .cmd shim, which Windows
    // cannot execute, so a bare name is only tried with a PATHEXT extension.
    let has_extension = extensions.iter().any(|e| ignore_case::ends_with(command, e));
    let names: Vec<String> = if has_extension {
        vec![command.to_owned()]
    } else {
        extensions.iter().map(|e| format!("{command}{e}")).collect()
    };

    if windows_path::is_rooted(command) {
        return names.into_iter().find(|name| file_exists(name));
    }

    for directory in split_list(path_variable.unwrap_or("")) {
        for name in &names {
            // C# guards Path.Combine against an ArgumentException, which .NET no longer throws for
            // odd characters; there is nothing to guard here.
            let candidate = windows_path::combine(directory.trim_matches('"'), name);
            if file_exists(&candidate) {
                return Some(candidate);
            }
        }
    }

    None
}

/// [`find_on_path`] with this process's `PATH` and `PATHEXT` and the real file system.
pub fn find_on_path_here(command: &str) -> Option<String> {
    find_on_path(command, std::env::var("PATH").ok().as_deref(), std::env::var("PATHEXT").ok().as_deref(), |path| {
        std::path::Path::new(path).is_file()
    })
}

/// Builds a command line for CreateProcess. Batch files are run through cmd.exe, as they cannot be started directly.
pub fn build_command_line<S: AsRef<str>>(executable_path: &str, arguments: &[S]) -> String {
    let direct = std::iter::once(executable_path)
        .chain(arguments.iter().map(AsRef::as_ref))
        .map(quote_argument)
        .collect::<Vec<_>>()
        .join(" ");

    let extension = windows_path::extension(executable_path);
    let is_batch = ignore_case::eq(extension, ".cmd") || ignore_case::eq(extension, ".bat");

    // With /s, cmd strips exactly the outer pair of quotes and runs the rest verbatim.
    if is_batch { format!("cmd.exe /d /s /c \"{direct}\"") } else { direct }
}

/// Quotes one argument following the rules the C runtime uses to split a command line.
pub fn quote_argument(argument: &str) -> String {
    if !argument.is_empty() && !argument.chars().any(|c| c.is_whitespace() || c == '"') {
        return argument.to_owned();
    }

    let mut quoted = String::from("\"");
    let mut backslashes = 0;
    for c in argument.chars() {
        if c == '\\' {
            backslashes += 1;
            continue;
        }

        if c == '"' {
            // Backslashes before a quote must be doubled, then the quote itself escaped.
            quoted.push_str(&"\\".repeat(backslashes * 2 + 1));
        } else {
            quoted.push_str(&"\\".repeat(backslashes));
        }

        quoted.push(c);
        backslashes = 0;
    }

    // Trailing backslashes precede the closing quote, so they are doubled too.
    quoted.push_str(&"\\".repeat(backslashes * 2));
    quoted.push('"');
    quoted
}

#[cfg(test)]
mod tests {
    use super::*;

    const PATH: &str = r"C:\Windows;C:\Tools;C:\Users\me\AppData\Roaming\npm";
    const PATH_EXT: &str = ".COM;.EXE;.BAT;.CMD";

    fn files(existing: &[&'static str]) -> impl Fn(&str) -> bool {
        let existing = existing.to_vec();
        move |candidate| existing.iter().any(|e| ignore_case::eq(e, candidate))
    }

    #[test]
    fn finds_an_exe_on_the_path() {
        let found = find_on_path("codex", Some(PATH), Some(PATH_EXT), files(&[r"C:\Tools\codex.exe"]));
        assert!(ignore_case::eq(&found.unwrap(), r"C:\Tools\codex.exe"));
    }

    #[test]
    fn prefers_the_cmd_shim_over_an_extensionless_script() {
        let files = files(&[r"C:\Users\me\AppData\Roaming\npm\codex", r"C:\Users\me\AppData\Roaming\npm\codex.cmd"]);

        let found = find_on_path("codex", Some(PATH), Some(PATH_EXT), files);
        assert!(ignore_case::eq(&found.unwrap(), r"C:\Users\me\AppData\Roaming\npm\codex.cmd"));
    }

    #[test]
    fn returns_null_when_not_installed() {
        assert_eq!(find_on_path("claude", Some(PATH), Some(PATH_EXT), files(&[])), None);
    }

    #[test]
    fn rooted_commands_and_missing_pathext() {
        assert_eq!(
            find_on_path(r"C:\x\tool", None, None, files(&[r"C:\x\tool.bat"])).as_deref(),
            Some(r"C:\x\tool.BAT")
        );
        assert_eq!(
            find_on_path("tool.EXE", Some(r#" "C:\q" "#), Some("  "), files(&[r"C:\q\tool.EXE"])).as_deref(),
            Some(r"C:\q\tool.EXE")
        );
    }

    #[test]
    fn quotes_arguments_like_the_c_runtime() {
        for (argument, expected) in [
            ("plain", "plain"),
            ("two words", "\"two words\""),
            ("", "\"\""),
            ("say \"hi\"", "\"say \\\"hi\\\"\""),
            (r"C:\dir with space\", "\"C:\\dir with space\\\\\""),
        ] {
            assert_eq!(quote_argument(argument), expected, "{argument}");
        }
    }

    #[test]
    fn batch_files_run_through_cmd() {
        assert_eq!(
            build_command_line(r"C:\npm\codex.cmd", &["-c", "a b"]),
            "cmd.exe /d /s /c \"C:\\npm\\codex.cmd -c \"a b\"\""
        );
    }

    #[test]
    fn executables_run_directly() {
        assert_eq!(
            build_command_line(r"C:\Program Files\claude.exe", &["--settings", r"C:\s.json"]),
            "\"C:\\Program Files\\claude.exe\" --settings C:\\s.json"
        );
    }
}
