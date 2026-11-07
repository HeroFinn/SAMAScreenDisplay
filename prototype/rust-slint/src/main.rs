#![windows_subsystem = "windows"]

mod ipc;
mod service;

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
    let ui = AppWindow::new()?;

    // Local clock.
    let weak = ui.as_weak();
    let timer = slint::Timer::default();
    timer.start(slint::TimerMode::Repeated, Duration::from_secs(1), move || {
        if let Some(ui) = weak.upgrade() {
            ui.set_clock(chrono::Local::now().format("%H:%M:%S").to_string().into());
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

    // Poll the C# service over its named pipe.
    let weak = ui.as_weak();
    std::thread::spawn(move || loop {
        let result = ipc::status();
        let weak = weak.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = weak.upgrade() {
                match result {
                    Ok(st) => {
                        ui.set_starting(false);
                        ui.set_status_text("已连接".into());
                        ui.set_connected(st.connected);
                        ui.set_port(st.port.unwrap_or_else(|| "--".into()).into());
                        ui.set_firmware(st.version.unwrap_or_else(|| "--".into()).into());
                        ui.set_frames(st.frames_sent.to_string().into());
                        ui.set_frame_ms(format!("{:.0}", st.last_frame_ms).into());
                        ui.set_service_mode(st.service_mode);
                        ui.set_interval(format!("{} s", st.interval_seconds).into());
                        ui.set_interval_value(st.interval_seconds as f32);
                        ui.set_brightness(st.brightness as f32);
                        ui.set_theme(match st.theme.to_ascii_lowercase().as_str() {
                            "neon" => 1,
                            "minimal" | "light" => 2,
                            _ => 0,
                        });
                        ui.set_cpu_temp(temp(st.sensors.cpu_temp).into());
                        ui.set_cpu_load(load(st.sensors.cpu_load).into());
                        ui.set_cpu_clock(clock_mhz(st.sensors.cpu_clock_mhz).into());
                        ui.set_gpu_temp(temp(st.sensors.gpu_temp).into());
                        ui.set_gpu_load(load(st.sensors.gpu_load).into());
                        ui.set_gpu_clock(clock_mhz(st.sensors.gpu_clock_mhz).into());
                        ui.set_ram_percent(load(st.sensors.ram_percent).into());
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
            let _ = ipc::command(r#"{"Cmd":"start"}"#);
        });
    });

    ui.on_toggle_service(|| println!("toggle service requested"));

    ui.set_clock(chrono::Local::now().format("%H:%M:%S").to_string().into());
    if let Ok(page) = std::env::var("PAGE") {
        ui.set_page(page.parse::<i32>().unwrap_or(0));
    }
    ui.run()
}
