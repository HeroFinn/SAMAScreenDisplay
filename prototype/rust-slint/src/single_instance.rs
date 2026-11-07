use std::sync::atomic::{AtomicIsize, Ordering};
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, WAIT_OBJECT_0,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, CreateMutexW, OpenEventW, SetEvent, WaitForSingleObject, EVENT_MODIFY_STATE,
    INFINITE,
};

const MUTEX_NAME: &str = "Local\\SamaDisplay.Slint";
const EVENT_NAME: &str = "Local\\SamaDisplay.Slint.Activate";

static ACTIVATE_EVENT: AtomicIsize = AtomicIsize::new(0);

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Returns true for the primary instance. A secondary instance signals the
/// primary window to come to front and returns false.
pub fn acquire() -> bool {
    let mutex = wide(MUTEX_NAME);
    unsafe {
        let handle = CreateMutexW(std::ptr::null(), 0, mutex.as_ptr());
        if handle.is_null() {
            return true;
        }
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let event = wide(EVENT_NAME);
            let ev = OpenEventW(EVENT_MODIFY_STATE, 0, event.as_ptr());
            if !ev.is_null() {
                SetEvent(ev);
                CloseHandle(ev);
            }
            CloseHandle(handle);
            return false;
        }
        std::mem::forget(handle);
    }

    let event = wide(EVENT_NAME);
    unsafe {
        let ev = CreateEventW(std::ptr::null(), 0, 0, event.as_ptr());
        if !ev.is_null() {
            ACTIVATE_EVENT.store(ev as isize, Ordering::Relaxed);
        }
    }
    true
}

/// Wait for activation signals from later launches and bring the window back.
pub fn listen(ui: slint::Weak<crate::AppWindow>) {
    let handle = ACTIVATE_EVENT.load(Ordering::Relaxed);
    if handle == 0 {
        return;
    }
    std::thread::spawn(move || unsafe {
        let event = handle as *mut std::ffi::c_void;
        loop {
            let result = WaitForSingleObject(event, INFINITE);
            if result != WAIT_OBJECT_0 {
                break;
            }
            let weak = ui.clone();
            let _ = slint::invoke_from_event_loop(move || {
                use raw_window_handle::{HasWindowHandle, RawWindowHandle};
                use slint::ComponentHandle;
                use windows_sys::Win32::Graphics::Gdi::{InvalidateRect, UpdateWindow};
                use windows_sys::Win32::UI::WindowsAndMessaging::{
                    SetForegroundWindow, ShowWindow, SW_SHOW,
                };
                if let Some(ui) = weak.upgrade() {
                    let window_handle = ui.window().window_handle();
                    if let Ok(handle) = window_handle.window_handle() {
                        if let RawWindowHandle::Win32(win32) = handle.as_raw() {
                            let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                            unsafe {
                                ShowWindow(hwnd, SW_SHOW);
                                SetForegroundWindow(hwnd);
                                InvalidateRect(hwnd, std::ptr::null(), 1);
                                UpdateWindow(hwnd);
                            }
                            let _ = ui.show();
                            ui.window().request_redraw();
                        }
                    }
                }
            });
        }
    });
}

