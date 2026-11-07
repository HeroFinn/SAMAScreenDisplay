use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};
use winreg::RegKey;

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "SamaDisplaySlint";

fn current_exe() -> Option<String> {
    std::env::current_exe()
        .ok()
        .map(|path| path.display().to_string())
}

pub fn is_enabled() -> bool {
    let Some(exe) = current_exe() else { return false };
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey_with_flags(RUN_KEY, KEY_READ) else {
        return false;
    };
    key.get_value::<String, _>(VALUE_NAME)
        .map(|value| value.trim_matches('"').eq_ignore_ascii_case(&exe))
        .unwrap_or(false)
}

pub fn set_enabled(enabled: bool) -> bool {
    let Some(exe) = current_exe() else { return false };
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey_with_flags(RUN_KEY, KEY_WRITE) else {
        return false;
    };
    if enabled {
        key.set_value(VALUE_NAME, &format!("\"{exe}\"")).is_ok()
    } else {
        let _ = key.delete_value(VALUE_NAME);
        true
    }
}

pub fn toggle() -> bool {
    let next = !is_enabled();
    if set_enabled(next) {
        next
    } else {
        is_enabled()
    }
}
