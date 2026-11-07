use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

pub const SERVICE_NAME: &str = "SamaDisplaySvc";

/// Returns true when the service is already running and answering IPC.
pub fn is_ready() -> bool {
    crate::ipc::status().is_ok()
}

/// Starts the Windows service if needed and waits until its named pipe answers.
pub fn ensure_running() -> bool {
    if is_ready() {
        return true;
    }

    let _ = Command::new("sc.exe")
        .args(["start", SERVICE_NAME])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW
        .status();

    for _ in 0..24 {
        thread::sleep(Duration::from_millis(500));
        if is_ready() {
            return true;
        }
    }
    false
}
