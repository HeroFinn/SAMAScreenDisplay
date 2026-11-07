use std::thread;
use std::time::Duration;
use windows_sys::Win32::System::Services::{
    CloseServiceHandle, ControlService, OpenSCManagerW, OpenServiceW, QueryServiceStatus,
    StartServiceW, SC_MANAGER_CONNECT, SERVICE_CONTROL_STOP, SERVICE_QUERY_STATUS, SERVICE_RUNNING,
    SERVICE_START, SERVICE_STATUS, SERVICE_STOP,
};

pub const SERVICE_NAME: &str = "SamaDisplaySvc";

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Returns true when the service is already running and answering IPC.
pub fn is_ready() -> bool {
    crate::ipc::status().is_ok()
}

/// Starts the service through the Windows SCM API (no sc.exe / shell process).
fn start_service() -> bool {
    unsafe {
        let scm = OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT);
        if scm.is_null() {
            return false;
        }
        let name = wide(SERVICE_NAME);
        let service = OpenServiceW(scm, name.as_ptr(), SERVICE_START | SERVICE_QUERY_STATUS);
        if service.is_null() {
            CloseServiceHandle(scm);
            return false;
        }
        let started = StartServiceW(service, 0, std::ptr::null()) != 0;
        CloseServiceHandle(service);
        CloseServiceHandle(scm);
        started
    }
}

/// Query the SCM state without touching the named pipe.
pub fn is_running() -> bool {
    unsafe {
        let scm = OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT);
        if scm.is_null() {
            return false;
        }
        let name = wide(SERVICE_NAME);
        let service = OpenServiceW(scm, name.as_ptr(), SERVICE_QUERY_STATUS);
        if service.is_null() {
            CloseServiceHandle(scm);
            return false;
        }
        let mut status: SERVICE_STATUS = std::mem::zeroed();
        let ok = QueryServiceStatus(service, &mut status) != 0;
        let running = ok && status.dwCurrentState == SERVICE_RUNNING;
        CloseServiceHandle(service);
        CloseServiceHandle(scm);
        running
    }
}

/// Stop the Windows service through the SCM API.
pub fn stop() -> bool {
    unsafe {
        let scm = OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT);
        if scm.is_null() {
            return false;
        }
        let name = wide(SERVICE_NAME);
        let service = OpenServiceW(
            scm,
            name.as_ptr(),
            SERVICE_STOP | SERVICE_QUERY_STATUS,
        );
        if service.is_null() {
            CloseServiceHandle(scm);
            return false;
        }
        let mut status: SERVICE_STATUS = std::mem::zeroed();
        let ok = QueryServiceStatus(service, &mut status) != 0;
        let stopped = ok
            && status.dwCurrentState == SERVICE_RUNNING
            && ControlService(service, SERVICE_CONTROL_STOP, &mut status) != 0;
        CloseServiceHandle(service);
        CloseServiceHandle(scm);
        stopped
    }
}

/// Start or stop the service, used by the UI toggle.
pub fn toggle() -> bool {
    if is_running() {
        stop()
    } else {
        ensure_running()
    }
}

/// Starts the Windows service if needed and waits until its named pipe answers.
pub fn ensure_running() -> bool {
    if is_ready() {
        return true;
    }

    let _ = start_service();

    for _ in 0..24 {
        thread::sleep(Duration::from_millis(500));
        if is_ready() {
            return true;
        }
    }
    false
}

