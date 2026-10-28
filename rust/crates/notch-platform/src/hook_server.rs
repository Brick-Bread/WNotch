//! Listens on named pipes for the events `Notch.Hook.exe` and opencode's plugin forward from
//! agents. Ported from `src/Notch.Platform/Agents/AgentHookServer.cs`.
//!
//! One pipe is private to this process, for sessions Notch's own terminal started; the other is
//! shared by every Notch of this user, for agents that were started anywhere else.

use std::sync::Arc;
use std::time::Duration;

use notch_core::agents::hooks::{HookMessage, SHARED_PIPE_NAME, try_parse_hook_message};
use tokio::task::JoinHandle;

use crate::pipes;

const MAX_LINE_BYTES: usize = 256 * 1024;
const READ_TIMEOUT: Duration = Duration::from_secs(5);

/// Listens while it exists; dropping it stops both pipes.
pub struct HookServer {
    pipe_name: String,
    tasks: Vec<JoinHandle<()>>,
}

impl HookServer {
    /// Starts both listeners on the current tokio runtime. `received` is called for every event,
    /// including those from agents Notch did not start.
    pub fn start(received: impl Fn(HookMessage) + Send + Sync + 'static) -> Self {
        // Unique per process so two copies of the app (a dev build, say) never cross wires.
        let pipe_name = format!("notch-agent-{}", std::process::id());
        let received = Arc::new(received);
        let tasks = [pipe_name.clone(), SHARED_PIPE_NAME.to_owned()]
            .into_iter()
            .map(|name| {
                let received = Arc::clone(&received);
                // Duplex although nothing is ever sent back: opencode's runtime opens the pipe for
                // reading and writing and gives up when the server end only accepts input.
                tokio::spawn(pipes::serve(name, move |mut connection| {
                    let received = Arc::clone(&received);
                    async move {
                        let line =
                            tokio::time::timeout(READ_TIMEOUT, pipes::read_line(&mut connection, MAX_LINE_BYTES));
                        // A hook that disconnects early or stalls is simply dropped.
                        if let Ok(Ok(Some(line))) = line.await
                            && let Some(message) = try_parse_hook_message(&line)
                        {
                            received(message);
                        }
                    }
                }))
            })
            .collect();
        Self { pipe_name, tasks }
    }

    /// The private pipe's name, passed to Notch's terminal sessions in `NOTCH_PIPE`.
    pub fn pipe_name(&self) -> &str {
        &self.pipe_name
    }
}

impl Drop for HookServer {
    fn drop(&mut self) {
        for task in &self.tasks {
            task.abort();
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Mutex;

    use notch_core::agents::hooks::format_message;
    use tokio::io::AsyncWriteExt;

    use super::*;

    #[tokio::test]
    async fn a_hook_line_on_the_private_pipe_arrives() {
        let messages = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&messages);
        let server = HookServer::start(move |message| sink.lock().unwrap().push(message));

        let mut client = pipes::connect(server.pipe_name(), Duration::from_secs(5)).await.unwrap();
        let line = format_message("session-1", r#"{"hook_event_name":"Stop","cwd":"C:\\code"}"#, 42);
        client.write_all(format!("{line}\n").as_bytes()).await.unwrap();
        drop(client);

        for _ in 0..50 {
            if !messages.lock().unwrap().is_empty() {
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        let messages = messages.lock().unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(
            (messages[0].session.as_str(), messages[0].event_name.as_str(), messages[0].hook_pid),
            ("session-1", "Stop", 42)
        );
    }
}
