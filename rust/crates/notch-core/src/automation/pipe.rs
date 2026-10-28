//! What goes over the command pipe. Ported from the message format in
//! `src/Notch.Core/Automation/CommandPipe.cs`.
//!
//! A second copy of Notch, a `notch://` link and the command line tool talk to the notch that is
//! already running: one request line (a JSON array of arguments), one answer line. The pipe
//! belongs to the current Windows user only.
//!
//! Only the format and its checks are here, as functions over bytes and lines. The named pipe
//! itself (name [`DEFAULT_NAME`], in and out, byte mode, as many instances as Windows allows,
//! current user only) and its timeouts live in the Windows crate. A server there:
//! 1. reads bytes until [`request_line`] says the line is done, for at most [`READ_TIMEOUT`];
//! 2. turns the line into arguments with [`read_request`], or answers its error;
//! 3. writes [`answer_line`] of the handler's result. A caller that disconnects early or stalls is
//!    simply dropped.
//!
//! A client writes [`request_bytes`], reads one line as `StreamReader.ReadLine` does (up to `\n`,
//! `\r` or `\r\n`, invalid UTF-8 replaced) and passes it, or `None` at end of stream, to
//! [`read_answer`].

use std::time::Duration;

use serde_json::Value;

/// Debug builds use their own pipe so they can run next to an installed copy.
pub const DEFAULT_NAME: &str = if cfg!(debug_assertions) { "notch-command-debug" } else { "notch-command" };

pub const MAX_REQUEST_BYTES: usize = 8 * 1024;
pub const READ_TIMEOUT: Duration = Duration::from_secs(5);

const UNREADABLE: &str = "That request could not be read.";

/// What the running notch answered to a command sent through the pipe.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandResult {
    pub ok: bool,
    pub message: Option<String>,
}

impl CommandResult {
    pub fn new(ok: bool, message: Option<String>) -> Self {
        Self { ok, message }
    }
}

/// The request a client sends: its arguments as a JSON array of strings, and a line break.
pub fn request_bytes(arguments: &[String]) -> Vec<u8> {
    // Writing strings to JSON cannot fail.
    let mut bytes = serde_json::to_vec(arguments).unwrap_or_default();
    bytes.push(b'\n');
    bytes
}

/// How far the server has got reading a request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RequestLine {
    /// Keep reading.
    Incomplete,
    /// The request line, read up to the first line break or the end of the stream; `None` when
    /// it is empty or larger than [`MAX_REQUEST_BYTES`].
    Done(Option<String>),
}

/// Looks for the end of the request line in the bytes received so far. `closed` says the client
/// has finished sending. Bytes after the first line break are ignored.
pub fn request_line(received: &[u8], closed: bool) -> RequestLine {
    let decode = |bytes: &[u8]| (!bytes.is_empty()).then(|| String::from_utf8_lossy(bytes).into_owned());

    // C# reads one byte at a time while it holds no more than the limit, so a line break may be
    // the byte just after a full request.
    let window = &received[..received.len().min(MAX_REQUEST_BYTES + 1)];
    if let Some(end) = window.iter().position(|&b| b == b'\n') {
        return RequestLine::Done(decode(&received[..end]));
    }

    if received.len() > MAX_REQUEST_BYTES {
        RequestLine::Done(None)
    } else if closed {
        RequestLine::Done(decode(received))
    } else {
        RequestLine::Incomplete
    }
}

/// The arguments in a request line, or the answer for a request that cannot be read.
pub fn read_request(line: Option<&str>) -> Result<Vec<String>, CommandResult> {
    // `[null]` would hand C#'s handler a null argument, which it does not expect; it is refused here.
    match line.map(serde_json::from_str::<Option<Vec<String>>>) {
        Some(Ok(Some(arguments))) => Ok(arguments),
        _ => Err(CommandResult::new(false, Some(UNREADABLE.to_owned()))),
    }
}

/// The answer line: `{"ok":bool,"message":string|null}` and a line break.
pub fn answer_line(result: &CommandResult) -> Vec<u8> {
    let json = serde_json::json!({ "ok": result.ok, "message": result.message });
    let mut bytes = json.to_string().into_bytes();
    bytes.push(b'\n');
    bytes
}

/// Reads the server's answer line as System.Text.Json's web defaults do: names ignore case,
/// unknown ones are skipped. A stream that ends without a line counts as ok. `None` when the
/// answer cannot be read, as when none came.
pub fn read_answer(line: Option<&str>) -> Option<CommandResult> {
    let Some(line) = line else {
        return Some(CommandResult::new(true, None));
    };

    let Ok(Value::Object(map)) = serde_json::from_str::<Value>(line) else {
        return None;
    };

    let mut result = CommandResult::new(false, None);
    for (name, value) in &map {
        if name.eq_ignore_ascii_case("ok") {
            result.ok = value.as_bool()?;
        } else if name.eq_ignore_ascii_case("message") {
            result.message = match value {
                Value::Null => None,
                Value::String(message) => Some(message.clone()),
                _ => return None,
            };
        }
    }
    Some(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_request_is_one_line_of_json() {
        let bytes = request_bytes(&["timer".into(), "5m".into()]);
        assert_eq!(bytes, b"[\"timer\",\"5m\"]\n");
        let RequestLine::Done(line) = request_line(&bytes, false) else { panic!("the line is complete") };
        assert_eq!(read_request(line.as_deref()), Ok(vec!["timer".to_owned(), "5m".to_owned()]));
    }

    #[test]
    fn the_request_ends_at_the_line_break_or_the_end_of_the_stream() {
        assert_eq!(request_line(b"[\"a\"", false), RequestLine::Incomplete);
        assert_eq!(request_line(b"[\"a\"]", true), RequestLine::Done(Some("[\"a\"]".into())));
        assert_eq!(request_line(b"[]\nmore", false), RequestLine::Done(Some("[]".into())));
        assert_eq!(request_line(b"", true), RequestLine::Done(None));
        assert_eq!(request_line(b"\n", false), RequestLine::Done(None));
    }

    #[test]
    fn a_request_over_the_limit_is_not_read() {
        let mut full = vec![b' '; MAX_REQUEST_BYTES];
        full.push(b'\n');
        assert!(matches!(request_line(&full, false), RequestLine::Done(Some(_))));

        let over = vec![b' '; MAX_REQUEST_BYTES + 1];
        assert_eq!(request_line(&over, false), RequestLine::Done(None));
    }

    #[test]
    fn an_unreadable_request_gets_the_could_not_be_read_answer() {
        let unreadable = Err(CommandResult::new(false, Some("That request could not be read.".into())));
        for line in [None, Some("null"), Some("nope"), Some("[1]"), Some("[null]"), Some("{}"), Some("[] x")] {
            assert_eq!(read_request(line), unreadable, "{line:?}");
        }
    }

    #[test]
    fn the_answer_uses_web_naming() {
        assert_eq!(answer_line(&CommandResult::new(true, None)), b"{\"ok\":true,\"message\":null}\n");
        assert_eq!(
            answer_line(&CommandResult::new(false, Some("No.".into()))),
            b"{\"ok\":false,\"message\":\"No.\"}\n"
        );
    }

    #[test]
    fn the_client_reads_the_answer() {
        assert_eq!(read_answer(None), Some(CommandResult::new(true, None)));
        assert_eq!(
            read_answer(Some(r#"{"ok":false,"message":"No."}"#)),
            Some(CommandResult::new(false, Some("No.".into())))
        );
        assert_eq!(read_answer(Some(r#"{"OK":true,"extra":1}"#)), Some(CommandResult::new(true, None)));
        assert_eq!(read_answer(Some("")), None);
        assert_eq!(read_answer(Some("null")), None);
        assert_eq!(read_answer(Some(r#"{"ok":"true"}"#)), None);
    }
}
