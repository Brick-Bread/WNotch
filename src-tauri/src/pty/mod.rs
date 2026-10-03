//! Processes in pseudo terminals. [`Spawner`] is the seam the terminal sessions are built on:
//! the real one starts a ConPTY process on Windows and a PTY one elsewhere, and the tests
//! swap in their own.

use std::io::{self, Read};
use std::path::PathBuf;
use std::sync::Arc;

#[cfg(unix)]
mod unix;
#[cfg(windows)]
mod windows;

/// What to start, and how big its terminal is.
#[derive(Debug, Clone)]
pub struct SpawnRequest {
    pub executable: PathBuf,
    pub arguments: Vec<String>,
    /// The working directory.
    pub folder: String,
    /// Variables added to (or overriding) this process's environment.
    pub environment: Vec<(String, String)>,
    pub cols: u16,
    pub rows: u16,
}

/// A running process and its pseudo terminal.
pub trait PtyProcess: Send + Sync {
    /// Types into the terminal.
    fn write(&self, data: &[u8]) -> io::Result<()>;

    /// Tells the terminal its new size.
    fn resize(&self, cols: u16, rows: u16);

    /// Ends the process and everything it started.
    fn kill(&self);

    /// Blocks until the process has exited and returns its exit code. Afterwards the output
    /// stream ends once what is left of it has been read (the Windows pseudo console only ends
    /// it when told the process is gone).
    fn wait(&self) -> i64;
}

/// A started process and the stream of what it prints.
pub struct Spawned {
    pub process: Arc<dyn PtyProcess>,
    pub reader: Box<dyn Read + Send>,
}

/// Starts processes in pseudo terminals.
pub trait Spawner: Send + Sync {
    /// The message is meant for the user: it says why the process could not be started.
    fn spawn(&self, request: &SpawnRequest) -> Result<Spawned, String>;
}

/// The spawner for this platform.
pub fn system_spawner() -> Arc<dyn Spawner> {
    #[cfg(windows)]
    return Arc::new(windows::ConPtySpawner);
    #[cfg(unix)]
    return Arc::new(unix::PortableSpawner);
}

/// An OS error's text without the " (os error N)" Rust appends, which means nothing to the user.
#[cfg(any(windows, test))]
pub fn friendly_error(error: &io::Error) -> String {
    let text = error.to_string();
    match text.rfind(" (os error") {
        Some(cut) => text[..cut].trim_end_matches('.').to_owned() + ".",
        None => text,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn os_error_codes_are_dropped() {
        let error = io::Error::other("The directory name is invalid. (os error 267)");
        assert_eq!(friendly_error(&error), "The directory name is invalid.");
        assert_eq!(friendly_error(&io::Error::other("boom")), "boom");
    }
}
