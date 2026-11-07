use std::path::PathBuf;
use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn install_script(name: &str) -> Option<PathBuf> {
    let exe_dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
    for candidate in [
        exe_dir.join("install").join(name),
        exe_dir.join("..").join("install").join(name),
    ] {
        if candidate.exists() {
            return Some(candidate);
        }
    }
    None
}

/// Run the PowerShell install/uninstall script elevated via ShellExecute "runas".
pub fn run_script(name: &str) -> Result<(), String> {
    let script = install_script(name)
        .ok_or_else(|| format!("找不到安装脚本 install\\{name}"))?;
    let params = format!(
        "-NoProfile -ExecutionPolicy Bypass -File \"{}\"",
        script.display()
    );
    let operation = wide("runas");
    let file = wide("powershell.exe");
    let parameters = wide(&params);
    let result = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            operation.as_ptr(),
            file.as_ptr(),
            parameters.as_ptr(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    };
    if result as isize > 32 {
        Ok(())
    } else {
        Err(format!("无法启动管理员安装程序，ShellExecute={}", result as isize))
    }
}
