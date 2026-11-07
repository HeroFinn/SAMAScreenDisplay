use slint::{ComponentHandle, Timer, TimerMode, Weak};
use std::time::Duration;
use tray_icon::menu::{Menu, MenuEvent, MenuItem};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};

use crate::AppWindow;

pub struct TrayHandle {
    _tray: TrayIcon,
    _timer: Timer,
}

fn icon_rgba() -> Vec<u8> {
    let size = 32u32;
    let mut data = vec![0u8; (size * size * 4) as usize];
    for y in 0..size {
        for x in 0..size {
            let i = ((y * size + x) * 4) as usize;
            let inside = x >= 3 && x < 29 && y >= 3 && y < 29;
            if inside {
                data[i] = 0x7c;
                data[i + 1] = 0x5c;
                data[i + 2] = 0xff;
                data[i + 3] = 0xff;
            }
        }
    }
    // small white "S"-like mark
    for y in 10..22u32 {
        for x in 12..20u32 {
            let i = ((y * size + x) * 4) as usize;
            data[i] = 0xff;
            data[i + 1] = 0xff;
            data[i + 2] = 0xff;
            data[i + 3] = 0xff;
        }
    }
    data
}

pub fn install(ui: Weak<AppWindow>) -> Option<TrayHandle> {
    let menu = Menu::new();
    let show = MenuItem::new("显示主界面", true, None);
    let hide = MenuItem::new("隐藏窗口", true, None);
    let quit = MenuItem::new("退出", true, None);
    menu.append(&show).ok()?;
    menu.append(&hide).ok()?;
    menu.append(&quit).ok()?;

    let icon = Icon::from_rgba(icon_rgba(), 32, 32).ok()?;
    let tray = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("SAMA Display")
        .with_icon(icon)
        .build()
        .ok()?;

    let show_id = show.id().clone();
    let hide_id = hide.id().clone();
    let quit_id = quit.id().clone();

    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_millis(200), move || {
        while let Ok(event) = MenuEvent::receiver().try_recv() {
            if let Some(ui) = ui.upgrade() {
                if event.id == show_id {
                    ui.show();
                } else if event.id == hide_id {
                    ui.hide();
                } else if event.id == quit_id {
                    std::process::exit(0);
                }
            }
        }
    });

    Some(TrayHandle {
        _tray: tray,
        _timer: timer,
    })
}
