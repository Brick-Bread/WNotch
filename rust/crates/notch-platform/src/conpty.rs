//! A process running inside a Windows pseudo console (ConPTY). Ported from
//! `src/Notch.Platform/Terminal/PseudoConsoleSession.cs`.
//!
//! Output arrives as raw UTF-8 terminal bytes; input is written the same way. Dropping the
//! session, or calling [`PseudoConsole::close`], kills the whole process tree.

use std::ffi::{OsStr, OsString, c_void};
use std::fs::File;
use std::io::{self, Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::Console::{COORD, ClosePseudoConsole, CreatePseudoConsole, HPCON, ResizePseudoConsole};
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation, SetInformationJobObject,
};
use windows::Win32::System::Pipes::CreatePipe;
use windows::Win32::System::Threading::{
    CREATE_UNICODE_ENVIRONMENT, CreateProcessW, DeleteProcThreadAttributeList, EXTENDED_STARTUPINFO_PRESENT,
    GetExitCodeProcess, INFINITE, InitializeProcThreadAttributeList, LPPROC_THREAD_ATTRIBUTE_LIST,
    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, PROCESS_INFORMATION, STARTF_USESTDHANDLES, STARTUPINFOEXW,
    UpdateProcThreadAttribute, WaitForSingleObject,
};
use windows::core::{PCWSTR, PWSTR};

/// A running pseudo console session.
pub struct PseudoConsole {
    shared: Arc<Shared>,
    process_id: u32,
}

struct Shared {
    gate: Mutex<State>,
}

struct State {
    input: Option<File>,
    console: Option<HPCON>,
    job: Option<OwnedHandle>,
    closed: bool,
}

// SAFETY: HPCON is a handle; every use of it is under the mutex.
unsafe impl Send for State {}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        // A panic while holding the lock leaves nothing half-done worth refusing over.
        self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn close_console(&self) {
        // Under the lock so a concurrent resize never touches a closed handle.
        if let Some(console) = self.lock().console.take() {
            // SAFETY: the handle came from CreatePseudoConsole and is closed once, here.
            unsafe { ClosePseudoConsole(console) };
        }
    }
}

/// Receives raw terminal bytes, as the pseudo console produces them.
pub type OutputHandler = Box<dyn FnMut(&[u8]) + Send>;

/// Receives the process's exit code, once.
pub type ExitHandler = Box<dyn FnOnce(i32) + Send>;

/// Where a session's output and exit go. Both are called on background threads.
pub struct Callbacks {
    pub output: OutputHandler,
    pub exited: ExitHandler,
}

impl PseudoConsole {
    /// Starts `command_line` (quoted as CreateProcess expects) in `working_directory`.
    /// `environment` adds to, or overrides, this process's variables.
    pub fn start(
        command_line: &str,
        working_directory: &str,
        columns: i32,
        rows: i32,
        environment: &[(String, String)],
        callbacks: Callbacks,
    ) -> io::Result<Self> {
        // The pseudo console reads `input_read` and writes `output_write`; it keeps its own copies.
        let (input_read, input_write) = pipe()?;
        let (output_read, output_write) = pipe()?;

        // SAFETY: both handles are open pipe ends.
        let console = unsafe {
            CreatePseudoConsole(
                coord(columns, rows),
                HANDLE(input_read.as_raw_handle()),
                HANDLE(output_write.as_raw_handle()),
                0,
            )?
        };
        drop((input_read, output_write));
        let console_guard = ConsoleGuard(Some(console));

        let mut attributes = AttributeList::new(console)?;
        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        // Explicitly empty standard handles, so the child uses the pseudo console even when this
        // process was itself started with redirected output.
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.lpAttributeList = attributes.list();

        // CreateProcessW may modify the command line buffer, so it needs a writable copy.
        let mut command: Vec<u16> = wide(OsStr::new(command_line));
        let environment_block = environment_block(environment);
        let directory = wide(OsStr::new(working_directory));
        let mut process = PROCESS_INFORMATION::default();

        // SAFETY: every buffer is NUL-terminated and outlives the call; `startup` is a properly
        // sized STARTUPINFOEXW with a live attribute list.
        unsafe {
            CreateProcessW(
                PCWSTR::null(),
                Some(PWSTR(command.as_mut_ptr())),
                None,
                None,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                Some(environment_block.as_ptr().cast::<c_void>()),
                PCWSTR(directory.as_ptr()),
                &startup.StartupInfo,
                &mut process,
            )?;
        }
        drop(attributes);

        // SAFETY: CreateProcessW returned these handles, which this code now owns.
        let (process_handle, thread_handle) = unsafe {
            (OwnedHandle::from_raw_handle(process.hProcess.0), OwnedHandle::from_raw_handle(process.hThread.0))
        };
        drop(thread_handle);

        let job = kill_on_close_job();
        if let Some(job) = &job {
            // SAFETY: both handles are open.
            unsafe {
                let _ = AssignProcessToJobObject(HANDLE(job.as_raw_handle()), HANDLE(process_handle.as_raw_handle()));
            }
        }

        let shared = Arc::new(Shared {
            gate: Mutex::new(State {
                input: Some(File::from(input_write)),
                console: console_guard.release(),
                job,
                closed: false,
            }),
        });

        let Callbacks { mut output, exited } = callbacks;
        let mut output_pipe = File::from(output_read);
        thread::Builder::new().name("ConPTY output".into()).spawn(move || {
            let mut buffer = vec![0u8; 16 * 1024];
            // Ends when the pseudo console is closed: the pipe then reports end of stream.
            while let Ok(read @ 1..) = output_pipe.read(&mut buffer) {
                output(&buffer[..read]);
            }
        })?;

        let waiter = Arc::clone(&shared);
        thread::Builder::new().name("ConPTY exit".into()).spawn(move || {
            let process = HANDLE(process_handle.as_raw_handle());
            let mut code = 0u32;
            // SAFETY: the handle stays open until `process_handle` drops at the end of this closure.
            unsafe {
                WaitForSingleObject(process, INFINITE);
                let _ = GetExitCodeProcess(process, &mut code);
            }
            drop(process_handle);

            // The output pipe only reports end of stream once the pseudo console is closed.
            waiter.close_console();
            exited(code as i32);
        })?;

        Ok(Self { shared, process_id: process.dwProcessId })
    }

    pub fn process_id(&self) -> u32 {
        self.process_id
    }

    /// Sends input. Ignored once the process has gone.
    pub fn write(&self, data: &[u8]) {
        let mut state = self.shared.lock();
        if state.closed {
            return;
        }
        if let Some(input) = state.input.as_mut() {
            // The process may have gone already; there is nothing to tell anyone.
            let _ = input.write_all(data).and_then(|()| input.flush());
        }
    }

    pub fn resize(&self, columns: i32, rows: i32) {
        let state = self.shared.lock();
        if let Some(console) = state.console.filter(|_| !state.closed) {
            // SAFETY: the console handle is open while it is in the state, which the lock guards.
            unsafe {
                let _ = ResizePseudoConsole(console, coord(columns, rows));
            }
        }
    }

    /// Ends the session: closing the job kills the process and everything it started.
    pub fn close(&self) {
        {
            let mut state = self.shared.lock();
            if state.closed {
                return;
            }
            state.closed = true;
            state.job = None;
            state.input = None;
        }
        self.shared.close_console();
    }
}

impl Drop for PseudoConsole {
    fn drop(&mut self) {
        self.close();
    }
}

fn coord(columns: i32, rows: i32) -> COORD {
    COORD { X: columns.clamp(2, i32::from(i16::MAX)) as i16, Y: rows.clamp(2, i32::from(i16::MAX)) as i16 }
}

fn pipe() -> windows::core::Result<(OwnedHandle, OwnedHandle)> {
    let (mut read, mut write) = (HANDLE::default(), HANDLE::default());
    // SAFETY: both out-pointers are valid; the handles are owned from here on.
    unsafe {
        CreatePipe(&mut read, &mut write, None, 0)?;
        Ok((OwnedHandle::from_raw_handle(read.0), OwnedHandle::from_raw_handle(write.0)))
    }
}

fn kill_on_close_job() -> Option<OwnedHandle> {
    // SAFETY: an unnamed job with default security; `info` is the size the call is told.
    unsafe {
        let job = CreateJobObjectW(None, PCWSTR::null()).ok()?;
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let _ = SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            (&raw const info).cast(),
            size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        );
        Some(OwnedHandle::from_raw_handle(job.0))
    }
}

/// Closes a pseudo console that never made it into a session.
struct ConsoleGuard(Option<HPCON>);

impl ConsoleGuard {
    fn release(mut self) -> Option<HPCON> {
        self.0.take()
    }
}

impl Drop for ConsoleGuard {
    fn drop(&mut self) {
        if let Some(console) = self.0.take() {
            // SAFETY: the handle came from CreatePseudoConsole and was not handed on.
            unsafe { ClosePseudoConsole(console) };
        }
    }
}

/// A one-entry thread attribute list that attaches a pseudo console to a new process.
struct AttributeList {
    buffer: Vec<u8>,
}

impl AttributeList {
    fn new(console: HPCON) -> windows::core::Result<Self> {
        let mut size = 0usize;
        // SAFETY: the first call only reports the size it needs, and fails doing so.
        unsafe {
            let _ = InitializeProcThreadAttributeList(None, 1, None, &mut size);
        }
        let mut list = Self { buffer: vec![0u8; size] };
        // SAFETY: `buffer` is `size` bytes; the console handle value is stored in the list and
        // stays valid until the process has been created.
        unsafe {
            InitializeProcThreadAttributeList(Some(list.list()), 1, None, &mut size)?;
            UpdateProcThreadAttribute(
                list.list(),
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                Some(console.0 as *const c_void),
                size_of::<HPCON>(),
                None,
                None,
            )?;
        }
        Ok(list)
    }

    fn list(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        LPPROC_THREAD_ATTRIBUTE_LIST(self.buffer.as_mut_ptr().cast())
    }
}

impl Drop for AttributeList {
    fn drop(&mut self) {
        // SAFETY: the list was initialised in `new`.
        unsafe { DeleteProcThreadAttributeList(self.list()) };
    }
}

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(Some(0)).collect()
}

/// This process's environment with `extra` laid over it, names compared ignoring case, sorted
/// as Windows sorts environment blocks, as `NAME=value\0` entries ending in a second `\0`.
fn environment_block(extra: &[(String, String)]) -> Vec<u16> {
    let mut variables: Vec<(OsString, OsString)> = std::env::vars_os().collect();
    for (name, value) in extra {
        variables.retain(|(existing, _)| !existing.eq_ignore_ascii_case(name));
        variables.push((name.into(), value.into()));
    }
    variables.sort_by_key(|(name, _)| name.to_string_lossy().to_uppercase());

    let mut block = Vec::new();
    for (name, value) in variables {
        block.extend(name.encode_wide());
        block.push(u16::from(b'='));
        block.extend(value.encode_wide());
        block.push(0);
    }
    block.push(0);
    block
}

#[cfg(test)]
mod tests {
    use std::sync::mpsc;
    use std::time::Duration;

    use super::*;

    #[test]
    fn runs_a_command_and_reports_its_output_and_exit_code() {
        let (sender, receiver) = mpsc::channel::<Vec<u8>>();
        let (exit_sender, exit_receiver) = mpsc::channel::<i32>();
        let directory = std::env::temp_dir();
        let session = PseudoConsole::start(
            "cmd.exe /d /c echo hello %NOTCH_TEST% & exit 3",
            &directory.to_string_lossy(),
            80,
            25,
            &[("NOTCH_TEST".into(), "from-notch".into())],
            Callbacks {
                output: Box::new(move |bytes| {
                    let _ = sender.send(bytes.to_vec());
                }),
                exited: Box::new(move |code| {
                    let _ = exit_sender.send(code);
                }),
            },
        )
        .expect("cmd.exe starts");
        assert!(session.process_id() > 0);

        assert_eq!(exit_receiver.recv_timeout(Duration::from_secs(20)), Ok(3));
        let mut output = Vec::new();
        while let Ok(chunk) = receiver.recv_timeout(Duration::from_secs(5)) {
            output.extend(chunk);
        }
        let text = String::from_utf8_lossy(&output);
        assert!(text.contains("hello from-notch"), "{text}");
    }

    #[test]
    fn extra_variables_override_ignoring_case() {
        let block = String::from_utf16_lossy(&environment_block(&[("path".into(), "C:\\only".into())]));
        let entries: Vec<&str> = block.split('\0').filter(|e| !e.is_empty()).collect();
        let paths: Vec<&&str> = entries.iter().filter(|e| e.to_uppercase().starts_with("PATH=")).collect();
        assert_eq!(paths, [&"path=C:\\only"]);
        assert!(block.ends_with("\0\0"));
    }
}
