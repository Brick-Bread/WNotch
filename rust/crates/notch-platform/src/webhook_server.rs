//! The local webhook: an HTTP server on this computer's loopback address only. It moves bytes;
//! who may call and what a request may say is decided by `notch_core::automation::webhook`.
//! Ported from `src/Notch.Platform/Automation/WebhookServer.cs`.

use std::convert::Infallible;
use std::future::Future;
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use http_body_util::{BodyExt, Full};
use hyper::body::{Bytes, Incoming};
use hyper::header::{AUTHORIZATION, CONTENT_LENGTH, CONTENT_TYPE, HOST, ORIGIN};
use hyper::server::conn::http1;
use hyper::service::service_fn;
use hyper::{Request, Response};
use hyper_util::rt::TokioIo;
use notch_core::automation::pipe::CommandResult;
use notch_core::automation::webhook::{self, MAX_BODY_BYTES, WebhookAction};
use tokio::net::TcpListener;
use tokio::task::JoinHandle;

const MAX_REQUESTS_PER_SECOND: u32 = 30;

/// A fixed window is enough to stop a runaway script or a guessing attack from hammering the app.
struct RateLimit {
    window_start: Instant,
    count: u32,
}

impl RateLimit {
    fn too_many(&mut self) -> bool {
        let now = Instant::now();
        if now - self.window_start >= Duration::from_secs(1) {
            self.window_start = now;
            self.count = 0;
        }
        self.count += 1;
        self.count > MAX_REQUESTS_PER_SECOND
    }
}

/// Serves while it exists; dropping it stops listening.
pub struct WebhookServer {
    task: JoinHandle<()>,
}

impl Drop for WebhookServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

type Handler =
    Arc<dyn Fn(WebhookAction) -> std::pin::Pin<Box<dyn Future<Output = CommandResult> + Send>> + Send + Sync>;

impl WebhookServer {
    /// Starts listening on `127.0.0.1:port` on the current tokio runtime. `Err` says why it could not.
    pub async fn start<H, F>(port: i32, token: String, handler: H) -> Result<Self, String>
    where
        H: Fn(WebhookAction) -> F + Send + Sync + 'static,
        F: Future<Output = CommandResult> + Send + 'static,
    {
        let port = match u16::try_from(port) {
            Ok(port @ 1024..) => port,
            _ => return Err("The webhook port must be between 1024 and 65535.".into()),
        };
        let listener = TcpListener::bind(SocketAddr::from((Ipv4Addr::LOCALHOST, port)))
            .await
            .map_err(|e| format!("The webhook could not listen on port {port}: {e}"))?;

        let handler: Handler = Arc::new(move |action| Box::pin(handler(action)));
        let limit = Arc::new(Mutex::new(RateLimit { window_start: Instant::now(), count: 0 }));
        let token = Arc::new(token);
        let task = tokio::spawn(async move {
            while let Ok((stream, _)) = listener.accept().await {
                let (handler, limit, token) = (Arc::clone(&handler), Arc::clone(&limit), Arc::clone(&token));
                tokio::spawn(async move {
                    let service = service_fn(move |request| {
                        let (handler, limit, token) = (Arc::clone(&handler), Arc::clone(&limit), Arc::clone(&token));
                        async move { Ok::<_, Infallible>(serve(request, &handler, &limit, &token, port).await) }
                    });
                    // The caller went away, or sent something that is not HTTP: nothing more to say.
                    let _ = http1::Builder::new().serve_connection(TokioIo::new(stream), service).await;
                });
            }
        });
        Ok(Self { task })
    }
}

async fn serve(
    request: Request<Incoming>,
    handler: &Handler,
    limit: &Mutex<RateLimit>,
    token: &str,
    port: u16,
) -> Response<Full<Bytes>> {
    if limit.lock().unwrap_or_else(|p| p.into_inner()).too_many() {
        return reply(429, "Too many requests.", None);
    }

    let header = |name| request.headers().get(name).and_then(|v: &hyper::header::HeaderValue| v.to_str().ok());
    if let Err(refusal) = webhook::authorize(header(HOST), header(ORIGIN), header(AUTHORIZATION), Some(token), port) {
        return reply(refusal.status, &refusal.message, None);
    }

    let declared: Option<u64> = header(CONTENT_LENGTH).and_then(|v| v.parse().ok());
    if declared.is_some_and(|length| length > MAX_BODY_BYTES as u64) {
        return reply(413, "The body is too large.", None);
    }

    let method = request.method().as_str().to_owned();
    let path = request.uri().path_and_query().map_or("/", |p| p.as_str()).to_owned();
    let has_body = declared.is_some_and(|length| length > 0) || request.headers().contains_key("transfer-encoding");

    let body = if has_body {
        // A caller may lie about its length, so the body is cut off while it is read.
        match read_body(request.into_body()).await {
            Some(body) => Some(body),
            None => return reply(413, "The body is too large.", None),
        }
    } else {
        None
    };

    match webhook::route(&method, &path, body.as_deref()) {
        Err(refusal) => reply(refusal.status, &refusal.message, None),
        Ok(action) => {
            let result = handler(action).await;
            let message = result.message.as_deref().unwrap_or(if result.ok { "ok" } else { "Not done." });
            reply(if result.ok { 200 } else { 422 }, message, Some(result.ok))
        }
    }
}

/// The body as text, or `None` when it grows past the limit.
async fn read_body(mut body: Incoming) -> Option<String> {
    let mut buffer = Vec::new();
    while let Some(frame) = body.frame().await {
        // A broken stream ends the body where it broke, as reading the C# stream would.
        let Ok(frame) = frame else { break };
        if let Some(data) = frame.data_ref() {
            buffer.extend_from_slice(data);
            if buffer.len() > MAX_BODY_BYTES {
                return None;
            }
        }
    }
    Some(String::from_utf8_lossy(&buffer).into_owned())
}

fn reply(status: u16, message: &str, ok: Option<bool>) -> Response<Full<Bytes>> {
    let body = serde_json::json!({ "ok": ok.unwrap_or(status < 300), "message": message }).to_string();
    // No CORS headers on purpose: a web page must not be able to read or send anything here.
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, "application/json; charset=utf-8")
        .body(Full::new(Bytes::from(body)))
        .unwrap_or_else(|_| Response::new(Full::new(Bytes::new())))
}

#[cfg(test)]
mod tests {
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpStream;

    use super::*;

    async fn send(port: u16, request: String) -> String {
        let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        stream.write_all(request.as_bytes()).await.unwrap();
        let mut answer = String::new();
        let _ = stream.read_to_string(&mut answer).await;
        answer
    }

    fn post(port: u16, token: &str, path: &str, body: &str) -> String {
        format!(
            "POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        )
    }

    async fn server(port: u16) -> WebhookServer {
        WebhookServer::start(i32::from(port), "secret".into(), |action| async move {
            CommandResult::new(true, Some(format!("{action:?}").chars().take(20).collect()))
        })
        .await
        .unwrap()
    }

    #[tokio::test]
    async fn a_good_request_is_handled_and_a_bad_token_is_refused() {
        let port = 47_000 + (std::process::id() % 1000) as u16;
        let _server = server(port).await;

        let answer = send(port, post(port, "secret", "/v1/notify", r#"{"title":"Hi"}"#)).await;
        assert!(answer.starts_with("HTTP/1.1 200"), "{answer}");
        assert!(answer.contains("application/json; charset=utf-8"));
        assert!(!answer.to_lowercase().contains("access-control"));

        let answer = send(port, post(port, "wrong", "/v1/notify", r#"{"title":"Hi"}"#)).await;
        assert!(answer.starts_with("HTTP/1.1 401"), "{answer}");
    }

    #[tokio::test]
    async fn a_large_body_or_a_page_is_refused() {
        let port = 48_000 + (std::process::id() % 1000) as u16;
        let _server = server(port).await;

        let big = "x".repeat(MAX_BODY_BYTES + 1);
        let answer = send(port, post(port, "secret", "/v1/notify", &big)).await;
        assert!(answer.starts_with("HTTP/1.1 413"), "{answer}");

        let from_page = format!(
            "POST /v1/notify HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: https://example.com\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}"
        );
        assert!(send(port, from_page).await.starts_with("HTTP/1.1 403"));
    }

    #[tokio::test]
    async fn ports_below_1024_are_refused() {
        let error = WebhookServer::start(80, "t".into(), |_| async { CommandResult::new(true, None) }).await.err();
        assert_eq!(error.as_deref(), Some("The webhook port must be between 1024 and 65535."));
    }
}
