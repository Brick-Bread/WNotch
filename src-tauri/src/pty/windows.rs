//! Windows pseudo consoles (ConPTY). Not `portable-pty`: that quotes arguments itself, which a
//! batch file run through `cmd /s` cannot undo, and it ends only the process it started, leaving
//! what that started (an npm shim's `node`) running. Here the command line is handed to
//! `CreateProcessW` as built, and a job object ends the whole tree.

use std::collections::BTreeMap;
use std::ffi::{c_void, OsStr, OsString};
use std::fs::File;
use std::io::{self, Write};
use std::mem::{size_of, zeroed};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr::{null, null_mut};
use std::sync::{Arc, Mutex, MutexGuard};

use notch_core::commandline::build_command_line;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Console::{
    ClosePseudoConsole, CreatePseudoConsole, ResizePseudoConsole, COORD, HPCON,
};
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
    TerminateJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CreateProcessW, DeleteProcThreadAttributeList, GetExitCodeProcess, InitializeProcThreadAttributeList,
    ResumeThread, TerminateProcess, UpdateProcThreadAttribute, WaitForSingleObject, CREATE_SUSPENDED,
    CREATE_UNICODE_ENVIRONMENT, EXTENDED_STARTUPINFO_PRESENT, INFINITE, PROCESS_INFORMATION,
    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, STARTF_USESTDHANDLES, STARTUPINFOEXW,
};

use super::{friendly_error, PtyProcess, SpawnRequest, Spawned, Spawner};

/// Starts processes in ConPTY pseudo consoles.
pub struct ConPtySpawner;

impl Spawner for ConPtySpawner {
    fn spawn(&self, request: &SpawnRequest) -> Result<Spawned, String> {
        let (process, reader) = ConPty::start(request).map_err(|e| friendly_error(&e))?;
        Ok(Spawned { process: Arc::new(process), reader: Box::new(reader) })
    }
}

/// A kernel handle that is closed on drop.
struct Handle(HANDLE);

// SAFETY: kernel handles may be used from any thread.
unsafe impl Send for Handle {}
// SAFETY: as above; the operations used on them are thread safe.
unsafe impl Sync for Handle {}

impl Handle {
    fn is_valid(&self) -> bool {
        !self.0.is_null() && self.0 != INVALID_HANDLE_VALUE
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        if self.is_valid() {
            // SAFETY: the handle is owned by this value and closed only here.
            unsafe { CloseHandle(self.0) };
        }
    }
}

/// A process running inside a pseudo console. Dropping it ends the process and what it started.
struct ConPty {
    /// `None` once closed. Under a lock so a resize never touches a closed console.
    console: Mutex<Option<HPCON>>,
    process: Handle,
    /// Kills everything in it when closed, which also happens if Notch itself dies.
    job: Handle,
    input: Mutex<File>,
}

impl ConPty {
    fn start(request: &SpawnRequest) -> io::Result<(Self, File)> {
        let (input_read, input_write) = pipe()?;
        let (output_read, output_write) = pipe()?;

        let mut console: HPCON = 0;
        // SAFETY: both handles are open pipe ends and `console` is a valid out pointer.
        let result = unsafe {
            CreatePseudoConsole(
                coord(request.cols, request.rows),
                input_read.as_raw_handle(),
                output_write.as_raw_handle(),
                0,
                &mut console,
            )
        };
        if result < 0 {
            return Err(io::Error::from_raw_os_error(hresult_to_win32(result)));
        }
        // The pseudo console holds its own copies of these two ends.
        drop(input_read);
        drop(output_write);

        // From here on the console is closed on every failure path.
        let mut guard = ConsoleGuard(Some(console));
        let (process, thread) = create_process(console, request)?;
        let job = assign_to_job(&process);
        // SAFETY: `thread` is the suspended main thread of the process just created.
        unsafe { ResumeThread(thread.0) };
        drop(thread);

        let pty = Self { console: Mutex::new(guard.0.take()), process, job, input: Mutex::new(File::from(input_write)) };
        Ok((pty, File::from(output_read)))
    }

    fn console(&self) -> MutexGuard<'_, Option<HPCON>> {
        self.console.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// The output pipe only reports its end once the pseudo console is closed.
    fn close_console(&self) {
        if let Some(console) = self.console().take() {
            // SAFETY: the console was open and is taken out so that this runs once.
            unsafe { ClosePseudoConsole(console) };
        }
    }
}

impl Drop for ConPty {
    fn drop(&mut self) {
        // The job handle closes after this (field order), ending anything still running.
        self.close_console();
    }
}

impl PtyProcess for ConPty {
    fn write(&self, data: &[u8]) -> io::Result<()> {
        let mut input = self.input.lock().unwrap_or_else(|e| e.into_inner());
        input.write_all(data)?;
        input.flush()
    }

    fn resize(&self, cols: u16, rows: u16) {
        if let Some(console) = *self.console() {
            // SAFETY: the console is open while the lock is held.
            unsafe { ResizePseudoConsole(console, coord(cols, rows)) };
        }
    }

    fn kill(&self) {
        // SAFETY: both handles are open for as long as this value lives.
        unsafe {
            if self.job.is_valid() {
                TerminateJobObject(self.job.0, 1);
            } else {
                TerminateProcess(self.process.0, 1);
            }
        }
    }

    fn wait(&self) -> i64 {
        let mut code = 0u32;
        // SAFETY: the process handle is open for as long as this value lives.
        unsafe {
            WaitForSingleObject(self.process.0, INFINITE);
            GetExitCodeProcess(self.process.0, &mut code);
        }
        self.close_console();
        // Exit codes are shown as signed numbers, so a crash reads as -1073741819.
        i64::from(code as i32)
    }
}

/// Closes a pseudo console that was not handed on.
struct ConsoleGuard(Option<HPCON>);

impl Drop for ConsoleGuard {
    fn drop(&mut self) {
        if let Some(console) = self.0.take() {
            // SAFETY: the console is open and nothing else owns it.
            unsafe { ClosePseudoConsole(console) };
        }
    }
}

fn pipe() -> io::Result<(OwnedHandle, OwnedHandle)> {
    let (mut read, mut write): (HANDLE, HANDLE) = (null_mut(), null_mut());
    // SAFETY: both out pointers are valid.
    if unsafe { CreatePipe(&mut read, &mut write, null(), 0) } == 0 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: CreatePipe returned two new handles that nothing else owns.
    Ok(unsafe { (OwnedHandle::from_raw_handle(read), OwnedHandle::from_raw_handle(write)) })
}

fn coord(cols: u16, rows: u16) -> COORD {
    let clamp = |n: u16| i16::try_from(n.max(2)).unwrap_or(i16::MAX);
    COORD { X: clamp(cols), Y: clamp(rows) }
}

/// The Win32 error inside an `HRESULT` (`0x8007xxxx`), which is what `io::Error` can describe.
fn hresult_to_win32(hresult: i32) -> i32 {
    if (hresult as u32) >> 16 == 0x8007 { hresult & 0xFFFF } else { hresult }
}

fn wide(text: impl AsRef<OsStr>) -> Vec<u16> {
    text.as_ref().encode_wide().chain(Some(0)).collect()
}

/// This process's environment with `extra` added or overriding, as the block `CreateProcessW`
/// takes. Names compare case-insensitively, as they do on Windows.
fn environment_block(extra: &[(String, String)]) -> Vec<u16> {
    let mut variables: BTreeMap<String, (OsString, OsString)> = BTreeMap::new();
    for (name, value) in std::env::vars_os() {
        variables.insert(name.to_string_lossy().to_uppercase(), (name, value));
    }
    for (name, value) in extra {
        variables.insert(name.to_uppercase(), (OsString::from(name), OsString::from(value)));
    }

    let mut block = Vec::new();
    for (name, value) in variables.values() {
        block.extend(name.encode_wide());
        block.push(u16::from(b'='));
        block.extend(value.encode_wide());
        block.push(0);
    }
    block.push(0);
    block
}

/// Deletes a process attribute list when it goes out of scope.
struct AttributeList(*mut c_void);

impl Drop for AttributeList {
    fn drop(&mut self) {
        // SAFETY: the list was initialised and is deleted once.
        unsafe { DeleteProcThreadAttributeList(self.0) };
    }
}

/// Starts the process suspended in `console`; returns it and its main thread.
fn create_process(console: HPCON, request: &SpawnRequest) -> io::Result<(Handle, Handle)> {
    let mut size = 0usize;
    // SAFETY: with a null list this only asks how large the list must be.
    unsafe { InitializeProcThreadAttributeList(null_mut(), 1, 0, &mut size) };
    // Aligned for a pointer, which the list holds.
    let mut storage = vec![0usize; size.div_ceil(size_of::<usize>())];
    let list = storage.as_mut_ptr().cast::<c_void>();
    // SAFETY: `list` has the room that was asked for.
    if unsafe { InitializeProcThreadAttributeList(list, 1, 0, &mut size) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let list = AttributeList(list);

    // SAFETY: the value is the pseudo console handle itself, as this attribute requires.
    let attribute = unsafe {
        UpdateProcThreadAttribute(
            list.0,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
            console as *const c_void,
            size_of::<HPCON>(),
            null_mut(),
            null(),
        )
    };
    if attribute == 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: STARTUPINFOEXW is plain data for which all zeroes is a valid empty value.
    let mut startup: STARTUPINFOEXW = unsafe { zeroed() };
    startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    // Explicitly empty standard handles, so the child uses the pseudo console even when this
    // process was itself started with redirected output.
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.lpAttributeList = list.0;

    let mut command_line = wide(build_command_line(&request.executable.to_string_lossy(), &request.arguments));
    let environment = environment_block(&request.environment);
    let folder = wide(&request.folder);
    // SAFETY: PROCESS_INFORMATION is plain data; zeroes are a valid empty value.
    let mut information: PROCESS_INFORMATION = unsafe { zeroed() };

    // SAFETY: every pointer refers to a buffer that outlives the call; `command_line` is
    // writable, as CreateProcessW requires.
    let created = unsafe {
        CreateProcessW(
            null(),
            command_line.as_mut_ptr(),
            null(),
            null(),
            0,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
            environment.as_ptr().cast(),
            if request.folder.is_empty() { null() } else { folder.as_ptr() },
            &startup.StartupInfo,
            &mut information,
        )
    };
    if created == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok((Handle(information.hProcess), Handle(information.hThread)))
}

/// A job that ends its processes when closed, with `process` in it. Invalid when jobs are
/// unavailable, in which case only the process itself can be ended.
fn assign_to_job(process: &Handle) -> Handle {
    // SAFETY: plain calls with valid arguments; the info struct is zeroed plain data.
    unsafe {
        let job = Handle(CreateJobObjectW(null(), null()));
        if !job.is_valid() {
            return job;
        }
        let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = zeroed();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let configured = SetInformationJobObject(
            job.0,
            JobObjectExtendedLimitInformation,
            (&raw const info).cast(),
            size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        );
        if configured == 0 || AssignProcessToJobObject(job.0, process.0) == 0 {
            return Handle(null_mut());
        }
        job
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn environment_variables_override_case_insensitively() {
        let block = environment_block(&[("path".to_owned(), "X".to_owned())]);
        let text = String::from_utf16_lossy(&block);
        let entries: Vec<&str> = text.split('\0').filter(|e| !e.is_empty()).collect();
        let paths: Vec<&&str> = entries.iter().filter(|e| e.to_uppercase().starts_with("PATH=")).collect();
        assert_eq!(paths, [&"path=X"]);
    }

    #[test]
    fn sizes_are_clamped_to_what_a_console_accepts() {
        assert_eq!((coord(0, 1).X, coord(0, 1).Y), (2, 2));
        assert_eq!(coord(u16::MAX, 24).X, i16::MAX);
    }
}
