//! How a second copy of Notch, a `notch://` link and `notchctl` talk to the notch that is already
//! running: one request line (a JSON array of arguments), one answer line, over a pipe that
//! belongs to the current user. Ported from the transport in
//! `src/Notch.Core/Automation/CommandPipe.cs`; the message format is `notch_core::automation::pipe`.

use std::future::Future;
use std::sync::Arc;
use std::time::Duration;

use notch_core::automation::pipe::{
    self, CommandResult, MAX_REQUEST_BYTES, READ_TIMEOUT, RequestLine, read_answer, read_request, request_line,
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::pipes;

/// How sending a command went: whether a notch was listening, and its answer if one came.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PipeExchange {
    pub connected: bool,
    pub result: Option<CommandResult>,
}

/// Answers requests on `name` (see [`pipe::DEFAULT_NAME`]) until the task running this is
/// dropped. `handler` gets each request's arguments.
pub async fn serve<H, F>(name: String, handler: H)
where
    H: Fn(Vec<String>) -> F + Send + Sync + 'static,
    F: Future<Output = CommandResult> + Send + 'static,
{
    let handler = Arc::new(handler);
    pipes::serve(name, move |mut connection| {
        let handler = Arc::clone(&handler);
        async move {
            let line = tokio::time::timeout(READ_TIMEOUT, read_request_line(&mut connection)).await;
            // A caller that stalls is simply dropped.
            let Ok(Ok(line)) = line else { return };
            let result = match read_request(line.as_deref()) {
                Ok(arguments) => handler(arguments).await,
                Err(refusal) => refusal,
            };
            let _ = connection.write_all(&pipe::answer_line(&result)).await;
            let _ = connection.flush().await;
        }
    })
    .await;
}

async fn read_request_line(stream: &mut (impl AsyncReadExt + Unpin)) -> std::io::Result<Option<String>> {
    let mut received = Vec::new();
    let mut chunk = [0u8; 1024];
    loop {
        let read = stream.read(&mut chunk).await?;
        received.extend_from_slice(&chunk[..read]);
        if let RequestLine::Done(line) = request_line(&received, read == 0) {
            return Ok(line);
        }
        if received.len() > MAX_REQUEST_BYTES {
            return Ok(None);
        }
    }
}

/// Sends the arguments to the running notch and waits for its answer. Some commands wait on the
/// user (a plugin's review, say), so once connected the answer may take up to `answer_timeout`.
pub async fn exchange(
    name: &str,
    arguments: &[String],
    connect_timeout: Duration,
    answer_timeout: Duration,
) -> PipeExchange {
    let Ok(mut client) = pipes::connect(name, connect_timeout).await else {
        return PipeExchange { connected: false, result: None };
    };

    let talk = async {
        client.write_all(&pipe::request_bytes(arguments)).await?;
        client.flush().await?;
        pipes::read_line(&mut client, usize::MAX).await
    };
    let result = match tokio::time::timeout(answer_timeout, talk).await {
        Ok(Ok(answer)) => read_answer(answer.as_deref()),
        _ => None,
    };
    PipeExchange { connected: true, result }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn a_command_goes_through_and_its_answer_comes_back() {
        let name = format!("notch-test-command-{}", std::process::id());
        let server = tokio::spawn(serve(name.clone(), |arguments: Vec<String>| async move {
            CommandResult::new(true, Some(format!("got {}", arguments.join(" "))))
        }));

        let exchange =
            exchange(&name, &["timer".into(), "5m".into()], Duration::from_secs(5), Duration::from_secs(5)).await;
        assert_eq!(
            exchange,
            PipeExchange { connected: true, result: Some(CommandResult::new(true, Some("got timer 5m".into()))) }
        );
        server.abort();
    }

    #[tokio::test]
    async fn no_notch_listening_is_not_connected() {
        let exchange = exchange("notch-test-nobody", &[], Duration::from_millis(200), Duration::from_secs(1)).await;
        assert_eq!(exchange, PipeExchange { connected: false, result: None });
    }
}
