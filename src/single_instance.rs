//! 單一執行個體 (Single-Instance) 檢測、重複開啟聚焦喚醒與跨版本更換啟動機制。
//! 使用具名 Mutex 與 Event 進行跨行程協調。

use std::path::PathBuf;
use std::sync::OnceLock;

use windows::core::{w, PCWSTR, PWSTR};
use windows::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0,
};
use windows::Win32::Storage::FileSystem::{
    GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW, VS_FIXEDFILEINFO,
};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::RemoteDesktop::ProcessIdToSessionId;
use windows::Win32::System::Threading::{
    CreateEventW, CreateMutexW, GetCurrentProcessId, OpenEventW, OpenProcess, QueryFullProcessImageNameW, ReleaseMutex,
    SetEvent, TerminateProcess, WaitForMultipleObjects, WaitForSingleObject, EVENT_MODIFY_STATE, INFINITE,
    PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE,
};
use windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow;

use crate::app_info;
use crate::i18n::{format as format_text, tr};
use crate::win::{ask_yes_no, show_message, MessageIcon};

// These three names are a cross-version contract: a new build has to
// recognise, signal and replace an OLD build that is already running, so none
// of them may ever carry the version number. MUTEX_NAME in particular must
// stay byte-for-byte what earlier releases used; the installer waits on it.
const MUTEX_NAME: PCWSTR = w!("WinBatLens_SingleInstance_Mutex");
const ACTIVATE_EVENT_NAME: PCWSTR = w!("WinBatLens_Activate_Event");
const EXIT_EVENT_NAME: PCWSTR = w!("WinBatLens_Exit_Event");

// Generous enough for a cold machine to finish releasing sensor handles, short
// enough that a wedged instance does not hang the launch forever.
const HANDOFF_TIMEOUT_MS: u32 = 8000;

struct OwnedMutex(HANDLE);

// A mutex handle may be used from any thread; ownership is tracked by the
// kernel per thread and only ever claimed and released on the main thread.
unsafe impl Send for OwnedMutex {}
unsafe impl Sync for OwnedMutex {}

static MUTEX: OnceLock<OwnedMutex> = OnceLock::new();

struct RunningInstance {
    process_id: u32,
    version: Option<String>,
}

/// True if this process should carry on starting; false if another instance
/// owns the session and the caller should shut down. Any user interaction
/// (version prompt, "already running" notice) has already happened by the
/// time this returns.
pub fn try_claim_ownership() -> bool {
    let Ok(mutex) = (unsafe { CreateMutexW(None, true, MUTEX_NAME) }) else {
        // Without the mutex there is nothing to coordinate on; start anyway.
        return true;
    };
    let already_exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    let _ = MUTEX.set(OwnedMutex(mutex));

    if !already_exists {
        return true;
    }

    let running = find_running_instance();

    // A version we could not read is treated as a match. Guessing wrong the
    // other way would offer to kill a process for no reason.
    if let Some(instance) = &running {
        if let Some(version) = instance.version.as_deref().filter(|v| *v != app_info::VERSION) {
            let question = format_text(tr("InstanceVersionText"), &[version, &app_info::display_version()]);
            if ask_yes_no(None, &question, tr("InstanceVersionTitle")) {
                if try_replace(instance, mutex) {
                    return true;
                }

                show_message(
                    None,
                    &format_text(tr("InstanceReplaceFailedText"), &[version]),
                    tr("InstanceReplaceFailedTitle"),
                    MessageIcon::Warning,
                );
            }
        }
    }

    activate_running_instance(running.as_ref());
    false
}

/// Starts listening for duplicate launches and for exit requests (a newer
/// build or the installer asking this one to stand down). Both callbacks fire
/// on a dedicated background thread.
pub fn start_listening(on_activate: impl Fn() + Send + 'static, on_exit_requested: impl Fn() + Send + 'static) {
    struct Events([HANDLE; 2]);
    unsafe impl Send for Events {}

    let events = unsafe {
        match (CreateEventW(None, false, false, ACTIVATE_EVENT_NAME), CreateEventW(None, false, false, EXIT_EVENT_NAME))
        {
            (Ok(activate), Ok(exit)) => Events([activate, exit]),
            // Losing the listener costs a nicety, not the app: duplicate
            // launches fall back to the message box path.
            _ => return,
        }
    };

    let spawned = std::thread::Builder::new().name("single-instance".into()).spawn(move || {
        let events = events;
        loop {
            let signalled = unsafe { WaitForMultipleObjects(&events.0, false, INFINITE) };
            match signalled.0.wrapping_sub(WAIT_OBJECT_0.0) {
                0 => on_activate(),
                1 => on_exit_requested(),
                _ => break,
            }
        }
    });
    let _ = spawned;
}

/// Gives the mutex up so a waiting successor (or the installer) can proceed.
pub fn release() {
    if let Some(mutex) = MUTEX.get() {
        unsafe {
            // Fails when this thread never owned the mutex — the normal case
            // for a duplicate launch on its way out.
            let _ = ReleaseMutex(mutex.0);
        }
    }
}

fn exe_file_name() -> Option<String> {
    let path: PathBuf = std::env::current_exe().ok()?;
    Some(path.file_name()?.to_string_lossy().to_lowercase())
}

fn find_running_instance() -> Option<RunningInstance> {
    let own_name = exe_file_name()?;
    let own_id = unsafe { GetCurrentProcessId() };
    let own_session = session_of(own_id)?;

    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }.ok()?;
    let mut entry = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
    let mut found = None;

    let mut more = unsafe { Process32FirstW(snapshot, &mut entry) }.is_ok();
    while more {
        let len = entry.szExeFile.iter().position(|&ch| ch == 0).unwrap_or(entry.szExeFile.len());
        let name = String::from_utf16_lossy(&entry.szExeFile[..len]).to_lowercase();

        // The mutex has no "Global\" prefix, so whoever owns it is in this
        // logon session; a copy under another login is not ours to touch.
        if name == own_name && entry.th32ProcessID != own_id && session_of(entry.th32ProcessID) == Some(own_session) {
            found =
                Some(RunningInstance { process_id: entry.th32ProcessID, version: read_version(entry.th32ProcessID) });
            break;
        }

        more = unsafe { Process32NextW(snapshot, &mut entry) }.is_ok();
    }

    unsafe {
        let _ = CloseHandle(snapshot);
    }
    found
}

fn session_of(process_id: u32) -> Option<u32> {
    let mut session = 0u32;
    unsafe { ProcessIdToSessionId(process_id, &mut session) }.ok().map(|()| session)
}

/// Reads the running instance's version off its EXE rather than asking the
/// process for it. That is the whole point: it works against builds that
/// predate this module and have no way to answer a question.
fn read_version(process_id: u32) -> Option<String> {
    let path = unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id).ok()?;
        let mut buffer = [0u16; 1024];
        let mut len = buffer.len() as u32;
        let queried = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buffer.as_mut_ptr()), &mut len);
        let _ = CloseHandle(process);
        queried.ok()?;
        buffer
    };

    unsafe {
        let path = PCWSTR(path.as_ptr());
        let size = GetFileVersionInfoSizeW(path, None);
        if size == 0 {
            return None;
        }

        let mut data = vec![0u8; size as usize];
        GetFileVersionInfoW(path, None, size, data.as_mut_ptr().cast()).ok()?;

        let mut info: *mut VS_FIXEDFILEINFO = std::ptr::null_mut();
        let mut info_len = 0u32;
        let found = VerQueryValueW(data.as_ptr().cast(), w!("\\"), (&raw mut info).cast(), &mut info_len);
        if !found.as_bool() || info.is_null() || (info_len as usize) < size_of::<VS_FIXEDFILEINFO>() {
            return None;
        }

        let info = &*info;
        let dotted = |ms: u32, ls: u32| format!("{}.{}.{}.{}", ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF);
        app_info::normalize(&dotted(info.dwProductVersionMS, info.dwProductVersionLS))
            .or_else(|| app_info::normalize(&dotted(info.dwFileVersionMS, info.dwFileVersionLS)))
    }
}

fn try_replace(running: &RunningInstance, mutex: HANDLE) -> bool {
    unsafe {
        let Ok(process) = OpenProcess(PROCESS_SYNCHRONIZE | PROCESS_TERMINATE, false, running.process_id) else {
            return false;
        };
        let wait_for_exit = || WaitForSingleObject(process, HANDOFF_TIMEOUT_MS) == WAIT_OBJECT_0;

        // Ask before killing. A build with an exit listener tears down its own
        // tray icon on the way out; terminating it strands the icon in the
        // notification area until the user happens to hover over it.
        let mut exited = try_signal(EXIT_EVENT_NAME) && wait_for_exit();

        if !exited {
            // Older builds have no exit listener, and closing the window is
            // useless here — it only hides to the tray.
            exited = TerminateProcess(process, 0).is_ok() && wait_for_exit();
        }
        let _ = CloseHandle(process);

        if !exited {
            return false;
        }

        // The dead owner left the mutex abandoned (or released it cleanly).
        // Claiming it is what actually makes this process the single instance.
        let claimed = WaitForSingleObject(mutex, HANDOFF_TIMEOUT_MS);
        claimed == WAIT_OBJECT_0 || claimed == WAIT_ABANDONED
    }
}

fn activate_running_instance(running: Option<&RunningInstance>) {
    // Windows only lets the current foreground process donate foreground
    // rights. Without this the other instance can flash in the taskbar but not
    // come to the front.
    if let Some(instance) = running {
        unsafe {
            let _ = AllowSetForegroundWindow(instance.process_id);
        }
    }

    // Works even when the instance is hidden in the tray.
    if try_signal(ACTIVATE_EVENT_NAME) {
        return;
    }

    // No listener, so there is nothing to raise. Say so rather than
    // disappearing.
    show_message(None, tr("InstanceRunningText"), tr("InstanceRunningTitle"), MessageIcon::Info);
}

fn try_signal(event_name: PCWSTR) -> bool {
    unsafe {
        // Fails against a build that has no listener, which is expected.
        let Ok(event) = OpenEventW(EVENT_MODIFY_STATE, false, event_name) else { return false };
        let signalled = SetEvent(event).is_ok();
        let _ = CloseHandle(event);
        signalled
    }
}
