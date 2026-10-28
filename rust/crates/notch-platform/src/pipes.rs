//! Named pipes that belong to the current Windows user only, as .NET's
//! `PipeOptions.CurrentUserOnly` makes them. The command pipe, the agent hooks and the plugin host
//! all talk this way. Ported from the transport in `src/Notch.Core/Automation/CommandPipe.cs`
//! and `src/Notch.Platform/Agents/AgentHookServer.cs`.
//!
//! A server pipe grants access to the current user alone and names them its owner. A client
//! refuses a pipe whose owner is someone else, so another user cannot pose as Notch. Both match
//! what .NET does, so the C# `notchctl` and `Notch.Hook.exe` work with the Rust app, and the
//! other way round.

use std::ffi::c_void;
use std::future::Future;
use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::time::Duration;

use tokio::io::{AsyncRead, AsyncReadExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient, NamedPipeServer, ServerOptions};
use windows::Win32::Foundation::{ERROR_PIPE_BUSY, HANDLE, HLOCAL, LocalFree};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetSecurityInfo, SDDL_REVISION_1,
    SE_KERNEL_OBJECT,
};
use windows::Win32::Security::{
    EqualSid, GetTokenInformation, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES,
    TOKEN_QUERY, TOKEN_USER, TokenUser,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::{HSTRING, PWSTR};

/// The full path of a pipe called `name`.
pub fn path(name: &str) -> String {
    format!(r"\\.\pipe\{name}")
}

/// The current user's security id, as owned bytes.
struct UserSid {
    buffer: Vec<u8>,
}

impl UserSid {
    fn current() -> io::Result<Self> {
        // SAFETY: the token is closed when `_close` drops; the buffer is sized by the first call.
        unsafe {
            let mut token = HANDLE::default();
            OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)?;
            let _close = OwnedHandle::from_raw_handle(token.0);

            let mut size = 0u32;
            let _ = GetTokenInformation(token, TokenUser, None, 0, &mut size);
            let mut buffer = vec![0u8; size as usize];
            GetTokenInformation(token, TokenUser, Some(buffer.as_mut_ptr().cast()), size, &mut size)?;
            Ok(Self { buffer })
        }
    }

    fn sid(&self) -> PSID {
        // SAFETY: the buffer holds a TOKEN_USER, whose SID points into the same buffer.
        unsafe { (*self.buffer.as_ptr().cast::<TOKEN_USER>()).User.Sid }
    }

    fn to_text(&self) -> io::Result<String> {
        let mut text = PWSTR::null();
        // SAFETY: the SID is valid; the string is freed with LocalFree as the API asks.
        unsafe {
            ConvertSidToStringSidW(self.sid(), &mut text)?;
            let result = text.to_string().map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e));
            let _ = LocalFree(Some(HLOCAL(text.0.cast())));
            result
        }
    }
}

/// Opens one instance of a pipe server that only the current user may connect to.
pub fn create_server(name: &str, first_instance: bool) -> io::Result<NamedPipeServer> {
    let user = UserSid::current()?.to_text()?;
    // Owner and the only access entry: the current user, with full access.
    let sddl = HSTRING::from(format!("O:{user}D:P(A;;GA;;;{user})"));
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    // SAFETY: `descriptor` is freed with LocalFree below, after the pipe has copied it.
    unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(&sddl, SDDL_REVISION_1, &mut descriptor, None)?;
        let mut attributes = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor.0,
            bInheritHandle: false.into(),
        };
        let server = ServerOptions::new()
            .first_pipe_instance(first_instance)
            .create_with_security_attributes_raw(path(name), (&raw mut attributes).cast::<c_void>());
        let _ = LocalFree(Some(HLOCAL(descriptor.0)));
        server
    }
}

/// Connects to a pipe the current user owns, waiting up to `timeout` for a free instance.
pub async fn connect(name: &str, timeout: Duration) -> io::Result<NamedPipeClient> {
    let deadline = tokio::time::Instant::now() + timeout;
    let client = loop {
        match ClientOptions::new().open(path(name)) {
            Ok(client) => break client,
            Err(e) if e.raw_os_error() == Some(ERROR_PIPE_BUSY.0 as i32) => {}
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(e),
        }
        if tokio::time::Instant::now() >= deadline {
            return Err(io::ErrorKind::TimedOut.into());
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    };

    check_owner(&client)?;
    Ok(client)
}

fn check_owner(client: &NamedPipeClient) -> io::Result<()> {
    let user = UserSid::current()?;
    let mut owner = PSID::default();
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    // SAFETY: `owner` points into `descriptor`, which is freed after the comparison.
    unsafe {
        GetSecurityInfo(
            HANDLE(client.as_raw_handle()),
            SE_KERNEL_OBJECT,
            OWNER_SECURITY_INFORMATION,
            Some(&mut owner),
            None,
            None,
            None,
            Some(&mut descriptor),
        )
        .ok()?;
        let same = EqualSid(owner, user.sid()).is_ok();
        let _ = LocalFree(Some(HLOCAL(descriptor.0)));
        if same {
            Ok(())
        } else {
            Err(io::Error::new(io::ErrorKind::PermissionDenied, "the pipe belongs to another user"))
        }
    }
}

/// Accepts connections on `name` until the task running this is dropped or aborted, handing each
/// to `handle` on its own task so a slow client cannot hold up the next one. A failure to open
/// the pipe is retried after a quarter of a second, as the C# listeners do.
pub async fn serve<F, Fut>(name: String, handle: F)
where
    F: Fn(NamedPipeServer) -> Fut + Send + Sync + 'static,
    Fut: Future<Output = ()> + Send + 'static,
{
    let mut first = true;
    loop {
        let server = match create_server(&name, first) {
            Ok(server) => server,
            Err(_) => {
                tokio::time::sleep(Duration::from_millis(250)).await;
                continue;
            }
        };
        first = false;
        if server.connect().await.is_ok() {
            tokio::spawn(handle(server));
        }
    }
}

/// Reads up to the first line break. `None` when the stream ends before any byte, or when the
/// line runs past `max_bytes` (the caller refuses it). Invalid UTF-8 is replaced, as .NET's
/// decoder does.
pub async fn read_line(stream: &mut (impl AsyncRead + Unpin), max_bytes: usize) -> io::Result<Option<String>> {
    let mut bytes = Vec::new();
    let mut one = [0u8; 1];
    while bytes.len() <= max_bytes {
        if stream.read(&mut one).await? == 0 || one[0] == b'\n' {
            if bytes.is_empty() {
                return Ok(None);
            }
            // A CR before the LF belongs to the line ending, as StreamReader.ReadLine treats it.
            if bytes.last() == Some(&b'\r') {
                bytes.pop();
            }
            return Ok(Some(String::from_utf8_lossy(&bytes).into_owned()));
        }
        bytes.push(one[0]);
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use tokio::io::AsyncWriteExt;

    use super::*;

    fn unique(name: &str) -> String {
        format!("notch-test-{name}-{}", std::process::id())
    }

    #[tokio::test]
    async fn a_client_talks_to_a_server_of_the_same_user() {
        let name = unique("echo");
        let server_name = name.clone();
        let server = tokio::spawn(serve(server_name, |mut pipe| async move {
            let line = read_line(&mut pipe, 1024).await.unwrap().unwrap();
            pipe.write_all(format!("echo {line}\n").as_bytes()).await.unwrap();
        }));

        let mut client = connect(&name, Duration::from_secs(5)).await.unwrap();
        client.write_all(b"hello\r\n").await.unwrap();
        let answer = read_line(&mut client, 1024).await.unwrap();
        assert_eq!(answer.as_deref(), Some("echo hello"));
        server.abort();
    }

    #[tokio::test]
    async fn nobody_listening_times_out() {
        let error = connect(&unique("nobody"), Duration::from_millis(200)).await.unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::TimedOut);
    }

    #[tokio::test]
    async fn lines_longer_than_the_limit_are_refused() {
        let mut long: &[u8] = &[b'a'; 20];
        assert_eq!(read_line(&mut long, 10).await.unwrap(), None);
        let mut short: &[u8] = b"abc";
        assert_eq!(read_line(&mut short, 10).await.unwrap().as_deref(), Some("abc"));
        let mut empty: &[u8] = b"";
        assert_eq!(read_line(&mut empty, 10).await.unwrap(), None);
    }
}
