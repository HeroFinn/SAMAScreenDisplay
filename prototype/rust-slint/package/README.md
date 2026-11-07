# SAMA Display · Rust + Slint

## 安装

1. 右键 `install-rust.ps1` → **以管理员身份运行**。
2. 脚本会：
   - 安装/修复 `SamaDisplaySvc` 后台服务（自动启动，独占 COM9）
   - 安装 PawnIO（若缺失，CPU 温度需要）
   - 复制 Rust 前端到 `C:\Program Files\SamaDisplay\App\SamaDisplay.Slint.exe`
   - 创建开始菜单和桌面快捷方式
3. 双击 “SAMA Display (Rust)” 启动。

首次启动会自动拉起服务并等待连接；不需要手动开服务。

## 卸载

右键 `uninstall-rust.ps1` → 以管理员身份运行，会删除 Rust 前端、快捷方式和后台服务。

## 文件

- `sama-display-slint.exe`：Rust/Slint 前端（GUI 子系统，无控制台）
- `service/`：C# 后台服务（self-contained，不需要装 .NET）
- `install/`：服务安装脚本 + PawnIO 下载器
- `RUST-PROTOTYPE.md`：原型开发说明

## 要求

- Windows 10/11 x64
- 管理员权限仅用于安装/卸载服务与 PawnIO
- 面板 USB：VID_1D6B / PID_0134（COM9）
