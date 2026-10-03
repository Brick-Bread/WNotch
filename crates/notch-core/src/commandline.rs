//! Builds the single command-line string Windows' `CreateProcess` takes: C runtime quoting for
//! each argument, and `cmd.exe` around batch files (which cannot be started directly).
//!
//! Pure string work, so it is tested on every platform; only the Windows launcher uses it.

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

        // Backslashes before a quote must be doubled, then the quote itself escaped.
        let run = if c == '"' { backslashes * 2 + 1 } else { backslashes };
        quoted.extend(std::iter::repeat_n('\\', run));
        quoted.push(c);
        backslashes = 0;
    }

    // Trailing backslashes precede the closing quote, so they are doubled too.
    quoted.extend(std::iter::repeat_n('\\', backslashes * 2));
    quoted.push('"');
    quoted
}

/// Whether Windows can only run `executable` through `cmd.exe`.
pub fn is_batch_file(executable: &str) -> bool {
    let lowered = executable.to_lowercase();
    lowered.ends_with(".cmd") || lowered.ends_with(".bat")
}

/// Builds a command line for `CreateProcess`. Batch files are run through `cmd.exe`, as they
/// cannot be started directly.
///
/// With `/s`, `cmd` strips exactly the outer pair of quotes and runs the rest verbatim, so a
/// batch file or arguments with spaces survive. That only works when the line is handed to
/// `CreateProcess` as it is, which is why this builds the whole string instead of an argument list.
pub fn build_command_line<S: AsRef<str>>(executable: &str, arguments: &[S]) -> String {
    let direct = std::iter::once(executable)
        .chain(arguments.iter().map(AsRef::as_ref))
        .map(quote_argument)
        .collect::<Vec<_>>()
        .join(" ");

    if is_batch_file(executable) {
        format!("cmd.exe /d /s /c \"{direct}\"")
    } else {
        direct
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quotes_arguments_like_the_c_runtime() {
        for (argument, expected) in [
            ("plain", "plain"),
            ("two words", "\"two words\""),
            ("", "\"\""),
            ("say \"hi\"", "\"say \\\"hi\\\"\""),
            (r"C:\dir with space\", "\"C:\\dir with space\\\\\""),
            ("tab\there", "\"tab\there\""),
        ] {
            assert_eq!(quote_argument(argument), expected, "{argument:?}");
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
    fn batch_files_in_folders_with_spaces_stay_quoted_inside_the_outer_pair() {
        assert_eq!(
            build_command_line(r"C:\Program Files\npm\claude.CMD", &["--settings", r"C:\My Dir\s.json"]),
            "cmd.exe /d /s /c \"\"C:\\Program Files\\npm\\claude.CMD\" --settings \"C:\\My Dir\\s.json\"\""
        );
    }

    #[test]
    fn executables_run_directly() {
        assert_eq!(
            build_command_line(r"C:\Program Files\claude.exe", &["--settings", r"C:\s.json"]),
            "\"C:\\Program Files\\claude.exe\" --settings C:\\s.json"
        );
    }

    #[test]
    fn detects_batch_files_by_extension() {
        assert!(is_batch_file(r"C:\x\tool.bat"));
        assert!(is_batch_file("TOOL.CMD"));
        assert!(!is_batch_file("tool.exe"));
        assert!(!is_batch_file("cmd"));
    }
}
