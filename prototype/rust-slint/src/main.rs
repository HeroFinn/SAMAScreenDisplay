#![windows_subsystem = "windows"]

mod autostart;
mod installer;
mod ipc;
mod service;
mod single_instance;
mod tray;
mod window;

use base64::Engine;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::ComponentHandle;
use std::time::Duration;

slint::include_modules!();

fn temp(value: Option<f64>) -> String {
    value.map(|v| format!("{v:.0}")).unwrap_or_else(|| "--".into())
}

fn load(value: Option<f64>) -> String {
    value.map(|v| format!("{v:.0}")).unwrap_or_else(|| "--".into())
}

fn clock_mhz(value: Option<f64>) -> String {
    value
        .map(|v| format!("{:.1} GHz", v / 1000.0))
        .unwrap_or_else(|| "--".into())
}

fn main() -> Result<(), slint::PlatformError> {
    if std::env::args().any(|arg| arg == "--toggle-autostart") {
        autostart::toggle();
        return Ok(());
    }
    if std::env::args().any(|arg| arg == "--toggle-service") {
        let _ = service::toggle();
        return Ok(());
    }
    if !single_instance::acquire() {
        return Ok(());
    }
    let ui = AppWindow::new()?;
    let _tray = tray::install(ui.as_weak());
    single_instance::listen(ui.as_weak());
    ui.set_autostart(autostart::is_enabled());

    // Intercept WM_CLOSE: the X button hides the window to tray instead of quitting.
    let close_timer = slint::Timer::default();
    let weak_close = ui.as_weak();
    close_timer.start(slint::TimerMode::Repeated, Duration::from_millis(500), move || {
        if let Some(ui) = weak_close.upgrade() {
            let window_handle = ui.window().window_handle();
            if let Ok(handle) = window_handle.window_handle() {
                if let RawWindowHandle::Win32(win32) = handle.as_raw() {
                    let _ = window::install_close_to_tray(win32.hwnd.get());
                }
            }
        }
    });

    // Bring the backend up automatically; a service installed by our installer
    // grants normal users start/stop/query rights, so this does not need UAC.
    ui.set_starting(true);
    ui.set_status_text("正在启动服务".into());
    let weak_ensure = ui.as_weak();
    std::thread::spawn(move || {
        let ok = service::ensure_running();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = weak_ensure.upgrade() {
                ui.set_starting(false);
                if !ok {
                    ui.set_status_text("服务未启动".into());
                }
            }
        });
    });

    // Poll the C# service: status + a freshly rendered dashboard preview.
    let mut settings_synced = false;
    let weak = ui.as_weak();
    std::thread::spawn(move || loop {
        let result = match ipc::preview() {
            Ok(value) => Ok(value),
            Err(_) => ipc::status().map(|status| (status, String::new())),
        };
        let first_sync = !settings_synced && result.is_ok();
        if first_sync {
            settings_synced = true;
        }
        let weak = weak.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = weak.upgrade() {
                match result {
                    Ok((st, image_b64)) => {
                        ui.set_starting(false);
                        ui.set_status_text("已连接".into());
                        ui.set_connected(st.connected);
                        ui.set_port(st.port.unwrap_or_else(|| "--".into()).into());
                        ui.set_firmware(st.version.unwrap_or_else(|| "--".into()).into());
                        ui.set_frames(st.frames_sent.to_string().into());
                        ui.set_frame_ms(format!("{:.0}", st.last_frame_ms).into());
                        ui.set_service_mode(st.service_mode);
                        ui.set_interval(format!("{} s", st.interval_seconds).into());
                        if first_sync {
                            ui.set_interval_value(st.interval_seconds as f32);
                            ui.set_brightness(st.brightness as f32);
                            ui.set_theme(match st.theme.to_ascii_lowercase().as_str() {
                                "neon" => 1,
                                "minimal" | "light" => 2,
                                _ => 0,
                            });
                        }
                        ui.set_cpu_temp(temp(st.sensors.cpu_temp).into());
                        ui.set_cpu_load(load(st.sensors.cpu_load).into());
                        ui.set_cpu_clock(clock_mhz(st.sensors.cpu_clock_mhz).into());
                        ui.set_gpu_temp(temp(st.sensors.gpu_temp).into());
                        ui.set_gpu_load(load(st.sensors.gpu_load).into());
                        ui.set_gpu_clock(clock_mhz(st.sensors.gpu_clock_mhz).into());
                        ui.set_ram_percent(load(st.sensors.ram_percent).into());

                        if !image_b64.is_empty() {
                            if let Ok(bytes) =
                                base64::engine::general_purpose::STANDARD.decode(image_b64)
                            {
                                let path = std::env::temp_dir()
                                    .join(format!("sama_preview_{}.jpg", std::process::id()));
                                if std::fs::write(&path, &bytes).is_ok() {
                                    if let Ok(image) = slint::Image::load_from_path(&path) {
                                        ui.set_preview_image(image);
                                    }
                                }
                            }
                        }
                    }
                    Err(_) => {
                        ui.set_connected(false);
                        if !ui.get_starting() {
                            ui.set_status_text("未连接".into());
                        }
                    }
                }
            }
        });
        std::thread::sleep(Duration::from_secs(2));
    });

    // Refresh keyframe.
    ui.on_refresh(|| {
        std::thread::spawn(|| {
            let _ = ipc::command(r#"{"Cmd":"start"}"#);
        });
    });

    let weak_apply = ui.as_weak();
    ui.on_apply_settings(move || {
        let Some(ui) = weak_apply.upgrade() else { return };
        let theme = match ui.get_theme() {
            1 => "Neon",
            2 => "Minimal",
            _ => "Dark",
        }
        .to_string();
        let brightness = ui.get_brightness().round() as i32;
        let interval = ui.get_interval_value().round() as i32;
        let rotate = ui.get_rotate();
        std::thread::spawn(move || {
            let _ = ipc::command(&format!(r#"{{"Cmd":"theme","Text":"{theme}"}}"#));
            let _ = ipc::command(&format!(r#"{{"Cmd":"brightness","Value":{brightness}}}"#));
            let _ = ipc::command(&format!(r#"{{"Cmd":"interval","Value":{interval}}}"#));
            let _ = ipc::command(&format!(r#"{{"Cmd":"rotate","Value":{rotate}}}"#));
        });
    });

    ui.on_open_logs(|| {
        let _ = std::process::Command::new("explorer.exe")
            .arg(r"C:\ProgramData\SamaDisplay")
            .spawn();
    });

    ui.on_restart_service(|| {
        std::thread::spawn(|| {
            let _ = service::stop();
            std::thread::sleep(std::time::Duration::from_secs(1));
            let _ = service::ensure_running();
        });
    });

    ui.on_toggle_autostart({
        let weak = ui.as_weak();
        move || {
            let enabled = autostart::toggle();
            if let Some(ui) = weak.upgrade() {
                ui.set_autostart(enabled);
                ui.set_status_text(if enabled { "已开启开机启动" } else { "已关闭开机启动" }.into());
            }
        }
    });

    ui.on_install_service({
        let weak = ui.as_weak();
        move || {
            if let Some(ui) = weak.upgrade() {
                ui.set_status_text("等待管理员确认安装".into());
            }
            let _ = installer::run_script("install-service.ps1");
        }
    });

    ui.on_uninstall_service({
        let weak = ui.as_weak();
        move || {
            if let Some(ui) = weak.upgrade() {
                ui.set_status_text("等待管理员确认卸载".into());
            }
            let _ = installer::run_script("uninstall-service.ps1");
        }
    });

    ui.on_toggle_service(|| {
        std::thread::spawn(|| {
            let _ = service::toggle();
        });
    });

    if let Ok(page) = std::env::var("PAGE") {
        ui.set_page(page.parse::<i32>().unwrap_or(0));
    }
    ui.run()
}
