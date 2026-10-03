//! Hook mode: `notch --hook` is what the Claude and Codex CLIs run when something happens in a
//! session Notch started. It forwards the event to the running app and must never get in the
//! CLI's way: it prints nothing (Claude adds hook output to the conversation) and the process
//! always exits 0.

use std::io::{IsTerminal, Read, Write};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

use interprocess::local_socket::{prelude::*, GenericNamespaced, Stream};
use notch_core::agents::hooks::{format_message, PIPE_VARIABLE, SESSION_VARIABLE};

/// How long Claude may take to write its event to stdin before the hook gives up.
const STDIN_TIMEOUT: Duration = Duration::from_secs(2);

/// How long the hook keeps trying to reach the app.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(1);

/// Runs the hook. Every failure is swallowed: the CLI carries on whether or not the app heard it.
pub fn run() {
    let _ = forward();
}

fn forward() -> Option<()> {
    let pipe = std::env::var(PIPE_VARIABLE).ok().filter(|v| !v.is_empty())?;
    let session = std::env::var(SESSION_VARIABLE).ok().filter(|v| !v.is_empty())?;
    let args: Vec<String> = std::env::args().skip(1).collect();
    let payload = read_payload(&args)?;

    let mut stream = connect(&pipe)?;
    let line = format!("{}\n", format_message(&session, &payload));
    stream.write_all(line.as_bytes()).ok()?;
    stream.flush().ok()
}

/// Connects to the app, trying for a moment: a busy socket (the app is handling another hook)
/// frees up almost at once.
fn connect(pipe: &str) -> Option<Stream> {
    let deadline = Instant::now() + CONNECT_TIMEOUT;
    loop {
        let name = pipe.to_ns_name::<GenericNamespaced>().ok()?;
        match Stream::connect(name) {
            Ok(stream) => return Some(stream),
            Err(_) if Instant::now() < deadline => thread::sleep(Duration::from_millis(20)),
            Err(_) => return None,
        }
    }
}

/// Codex passes its notification as the last argument; Claude writes the event to stdin.
fn read_payload(args: &[String]) -> Option<String> {
    if let Some(last) = args.last().filter(|a| a.trim_start().starts_with('{')) {
        return Some(last.clone());
    }

    if std::io::stdin().is_terminal() {
        return None;
    }

    // Reading stdin blocks for as long as the parent keeps it open, hence the thread.
    let (sender, receiver) = mpsc::channel();
    thread::spawn(move || {
        let mut text = String::new();
        if std::io::stdin().read_to_string(&mut text).is_ok() {
            let _ = sender.send(text);
        }
    });

    let text = receiver.recv_timeout(STDIN_TIMEOUT).ok()?;
    let text = text.trim();
    (!text.is_empty()).then(|| text.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codex_payload_is_the_last_argument() {
        let args = vec!["--hook".to_owned(), r#"{"type":"agent-turn-complete"}"#.to_owned()];
        assert_eq!(read_payload(&args).as_deref(), Some(r#"{"type":"agent-turn-complete"}"#));
    }
}
