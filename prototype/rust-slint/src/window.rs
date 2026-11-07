use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallWindowProcW, SetWindowLongPtrW, ShowWindow, GWLP_WNDPROC, SW_HIDE, WM_CLOSE,
};

type WndProc = Option<unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT>;

static OLD_PROC: AtomicIsize = AtomicIsize::new(0);
static INSTALLED: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn close_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if msg == WM_CLOSE {
        let _ = ShowWindow(hwnd, SW_HIDE);
        return 0;
    }
    let old = OLD_PROC.load(Ordering::Relaxed);
    let proc: WndProc = std::mem::transmute(old);
    match proc {
        Some(proc) => CallWindowProcW(Some(proc), hwnd, msg, wparam, lparam),
        None => 0,
    }
}

/// Subclass the Slint window so the X button hides to tray instead of exiting.
pub fn install_close_to_tray(hwnd: isize) -> bool {
    if INSTALLED.load(Ordering::Relaxed) {
        return true;
    }
    unsafe {
        let hwnd = hwnd as HWND;
        if hwnd.is_null() {
            return false;
        }
        let old = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, close_proc as *const () as usize as isize);
        if old == 0 {
            return false;
        }
        OLD_PROC.store(old, Ordering::Relaxed);
        INSTALLED.store(true, Ordering::Relaxed);
        true
    }
}
