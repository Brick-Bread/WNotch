//! Facts about other programs. Ported from `src/Notch.Platform/Agents/ProcessDirectory.cs` and
//! the owner lookup in `src/Notch.Platform/Clipboard/ClipboardListener.cs`.

use std::ffi::c_void;
use std::os::windows::io::{FromRawHandle, OwnedHandle};

use windows::Wdk::System::Threading::{NtQueryInformationProcess, ProcessBasicInformation};
use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows::Win32::System::Threading::{
    IsWow64Process, OpenProcess, PROCESS_BASIC_INFORMATION, PROCESS_NAME_WIN32, PROCESS_QUERY_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_VM_READ, QueryFullProcessImageNameW,
};
use windows::core::{BOOL, PWSTR};

// Offsets into the 64-bit PEB and RTL_USER_PROCESS_PARAMETERS, which have not moved since Windows 7.
const PEB_PROCESS_PARAMETERS: usize = 0x20;
const PARAMETERS_CURRENT_DIRECTORY: usize = 0x38;

#[repr(C)]
#[derive(Default)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: usize,
}

fn open(pid: u32, access: windows::Win32::System::Threading::PROCESS_ACCESS_RIGHTS) -> Option<(HANDLE, OwnedHandle)> {
    if pid == 0 {
        return None;
    }
    // SAFETY: the handle is owned by the returned guard and closed when it drops.
    unsafe {
        let process = OpenProcess(access, false, pid).ok()?;
        Some((process, OwnedHandle::from_raw_handle(process.0)))
    }
}

/// Reads `size_of::<T>()` bytes at `address` in another process.
///
/// # Safety
/// `T` must be valid for any bit pattern.
unsafe fn read<T: Default>(process: HANDLE, address: usize) -> Option<T> {
    let mut value = T::default();
    // SAFETY: `value` is a writable `T`; a bad address makes the call fail, not write.
    unsafe {
        ReadProcessMemory(process, address as *const c_void, (&raw mut value).cast(), size_of::<T>(), None).ok()?;
    }
    Some(value)
}

/// The folder another program was started in, or `None` when it cannot be read. opencode's
/// plugin reports the folder a session works in, and this is how Notch tells which of several
/// opencode windows that is. Only programs of the same user can be read, and only 64-bit ones.
pub fn working_directory(pid: u32) -> Option<String> {
    let (process, _close) = open(pid, PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)?;

    // SAFETY: plain queries and reads of another process's memory into local values.
    unsafe {
        let mut wow64 = BOOL::default();
        if IsWow64Process(process, &mut wow64).is_ok() && wow64.as_bool() {
            return None;
        }

        let mut basic = PROCESS_BASIC_INFORMATION::default();
        let status = NtQueryInformationProcess(
            process,
            ProcessBasicInformation,
            (&raw mut basic).cast(),
            size_of::<PROCESS_BASIC_INFORMATION>() as u32,
            &mut 0,
        );
        if status.is_err() || basic.PebBaseAddress.is_null() {
            return None;
        }

        let parameters: usize = read(process, basic.PebBaseAddress as usize + PEB_PROCESS_PARAMETERS)?;
        if parameters == 0 {
            return None;
        }

        let path: UnicodeString = read(process, parameters + PARAMETERS_CURRENT_DIRECTORY)?;
        if path.buffer == 0 || path.length == 0 || path.length > 32_000 {
            return None;
        }

        let mut buffer = vec![0u16; usize::from(path.length) / 2];
        ReadProcessMemory(
            process,
            path.buffer as *const c_void,
            buffer.as_mut_ptr().cast(),
            usize::from(path.length),
            None,
        )
        .ok()?;

        let folder = String::from_utf16_lossy(&buffer);
        Some(if folder.chars().count() > 3 { folder.trim_end_matches('\\').to_owned() } else { folder })
    }
}

/// The program's name without `.exe`, as .NET's `Process.ProcessName` gives it, or `None`.
pub fn name(pid: u32) -> Option<String> {
    let (process, _close) = open(pid, PROCESS_QUERY_LIMITED_INFORMATION)?;
    let mut buffer = vec![0u16; 1024];
    let mut size = buffer.len() as u32;
    // SAFETY: `buffer` holds `size` characters.
    unsafe {
        QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buffer.as_mut_ptr()), &mut size).ok()?;
    }
    let path = String::from_utf16_lossy(&buffer[..size as usize]);
    let file = path.rsplit(['\\', '/']).next().unwrap_or(&path);
    let stem = match file.rsplit_once('.') {
        Some((stem, extension)) if extension.eq_ignore_ascii_case("exe") => stem,
        _ => file,
    };
    Some(stem.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_this_programs_own_folder_and_name() {
        let pid = std::process::id();
        let expected = std::env::current_dir().unwrap().to_string_lossy().trim_end_matches('\\').to_lowercase();
        assert_eq!(working_directory(pid).map(|f| f.to_lowercase()), Some(expected));
        let exe = std::env::current_exe().unwrap();
        assert_eq!(name(pid).as_deref(), exe.file_stem().and_then(|s| s.to_str()));
    }

    #[test]
    fn nothing_for_a_process_that_is_not_there() {
        assert_eq!(working_directory(0), None);
        assert_eq!(name(0), None);
    }
}
