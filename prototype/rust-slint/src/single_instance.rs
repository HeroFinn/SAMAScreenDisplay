use windows_sys::Win32::Foundation::{GetLastError, ERROR_ALREADY_EXISTS};
use windows_sys::Win32::System::Threading::CreateMutexW;

/// One instance per user session. The mutex handle is intentionally leaked for
/// the lifetime of the process.
pub fn acquire() -> bool {
    let name: Vec<u16> = "Local\\SamaDisplay.Slint\0".encode_utf16().collect();
    unsafe {
        let handle = CreateMutexW(std::ptr::null(), 0, name.as_ptr());
        if handle.is_null() {
            return true;
        }
        if GetLastError() == ERROR_ALREADY_EXISTS {
            return false;
        }
        std::mem::forget(handle);
        true
    }
}
