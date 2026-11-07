use serde::Deserialize;
use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};

const PIPE: &str = r"\\.\pipe\SamaDisplay.IPC";

#[derive(Debug, Deserialize, Default, Clone)]
#[serde(rename_all = "PascalCase", default)]
pub struct IpcResponse {
    pub ok: bool,
    pub error: Option<String>,
    pub message: Option<String>,
    pub status: Option<StatusDto>,
}

#[derive(Debug, Deserialize, Default, Clone)]
#[serde(rename_all = "PascalCase", default)]
pub struct StatusDto {
    pub connected: bool,
    pub port: Option<String>,
    pub version: Option<String>,
    pub displaying: bool,
    pub frames_sent: i64,
    pub last_frame_ms: f64,
    pub last_error: Option<String>,
    pub brightness: i32,
    pub interval_seconds: i32,
    pub theme: String,
    pub service_mode: bool,
    pub sensors: Sensors,
}

#[derive(Debug, Deserialize, Default, Clone)]
#[serde(rename_all = "PascalCase", default)]
pub struct Sensors {
    pub cpu_temp: Option<f64>,
    pub cpu_load: Option<f64>,
    pub cpu_clock_mhz: Option<f64>,
    pub gpu_temp: Option<f64>,
    pub gpu_load: Option<f64>,
    pub gpu_clock_mhz: Option<f64>,
    pub ram_percent: Option<f64>,
}

fn send_line(payload: &str) -> Result<String, String> {
    let mut file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(PIPE)
        .map_err(|e| format!("pipe open: {e}"))?;
    file.write_all(payload.as_bytes())
        .and_then(|_| file.write_all(b"\n"))
        .and_then(|_| file.flush())
        .map_err(|e| format!("pipe write: {e}"))?;

    let mut line = String::new();
    BufReader::new(file)
        .read_line(&mut line)
        .map_err(|e| format!("pipe read: {e}"))?;
    Ok(line)
}

pub fn status() -> Result<StatusDto, String> {
    let line = send_line(r#"{"Cmd":"status"}"#)?;
    let response: IpcResponse =
        serde_json::from_str(line.trim()).map_err(|e| format!("json: {e}"))?;
    if response.ok {
        response.status.ok_or_else(|| "missing status".to_string())
    } else {
        Err(response.error.unwrap_or_else(|| "request failed".to_string()))
    }
}

pub fn command(payload: &str) -> Result<(), String> {
    let line = send_line(payload)?;
    let response: IpcResponse =
        serde_json::from_str(line.trim()).map_err(|e| format!("json: {e}"))?;
    if response.ok {
        Ok(())
    } else {
        Err(response.error.unwrap_or_else(|| "request failed".to_string()))
    }
}
