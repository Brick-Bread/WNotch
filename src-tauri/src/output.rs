//! Gathers the many small chunks a terminal process prints so they reach the UI in few events.

use std::sync::{Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use base64::engine::general_purpose::STANDARD;
use base64::Engine;

/// How long output is held back in the hope that more follows.
pub const FLUSH_INTERVAL: Duration = Duration::from_millis(8);

/// The most bytes handed over in one batch; reaching it flushes at once.
pub const MAX_BATCH: usize = 64 * 1024;

#[derive(Default)]
struct State {
    data: Vec<u8>,
    closed: bool,
}

/// A buffer between a session's reader thread (which pushes) and its flusher thread (which
/// takes batches).
#[derive(Default)]
pub struct OutputQueue {
    state: Mutex<State>,
    ready: Condvar,
}

impl OutputQueue {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Adds output to the queue.
    pub fn push(&self, bytes: &[u8]) {
        self.lock().data.extend_from_slice(bytes);
        self.ready.notify_one();
    }

    /// Ends the queue: [`next_batch`](Self::next_batch) returns what is left, then `None`.
    pub fn close(&self) {
        self.lock().closed = true;
        self.ready.notify_all();
    }

    /// Waits for output, then for [`FLUSH_INTERVAL`] more unless a full batch is already there
    /// (or the queue closed), and takes up to [`MAX_BATCH`] bytes. `None` once the queue is
    /// closed and empty.
    pub fn next_batch(&self) -> Option<Vec<u8>> {
        let mut state = self.lock();
        while state.data.is_empty() {
            if state.closed {
                return None;
            }
            state = self.ready.wait(state).unwrap_or_else(|e| e.into_inner());
        }

        let deadline = Instant::now() + FLUSH_INTERVAL;
        while state.data.len() < MAX_BATCH && !state.closed {
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else { break };
            state = self.ready.wait_timeout(state, remaining).unwrap_or_else(|e| e.into_inner()).0;
        }

        let take = state.data.len().min(MAX_BATCH);
        Some(state.data.drain(..take).collect())
    }
}

/// The wire form of terminal output: base64 of the raw bytes.
pub fn encode(bytes: &[u8]) -> String {
    STANDARD.encode(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn small_pushes_are_merged() {
        let queue = OutputQueue::default();
        queue.push(b"ab");
        queue.push(b"cd");
        queue.close();
        assert_eq!(queue.next_batch(), Some(b"abcd".to_vec()));
        assert_eq!(queue.next_batch(), None);
    }

    #[test]
    fn batches_never_exceed_the_limit() {
        let queue = OutputQueue::default();
        queue.push(&vec![7; MAX_BATCH + 10]);
        queue.close();
        assert_eq!(queue.next_batch().map(|b| b.len()), Some(MAX_BATCH));
        assert_eq!(queue.next_batch().map(|b| b.len()), Some(10));
        assert_eq!(queue.next_batch(), None);
    }

    #[test]
    fn closing_wakes_a_waiting_reader() {
        let queue = std::sync::Arc::new(OutputQueue::default());
        let waiter = {
            let queue = queue.clone();
            std::thread::spawn(move || queue.next_batch())
        };
        std::thread::sleep(Duration::from_millis(20));
        queue.close();
        assert_eq!(waiter.join().expect("thread"), None);
    }

    #[test]
    fn encodes_raw_bytes_as_base64() {
        assert_eq!(encode(&[0x1b, b'[', 0xff]), "G1v/");
        assert_eq!(encode(b""), "");
    }
}
