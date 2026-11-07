# SAMA Display 独立实时版

## 已完成

- 新 WPF 前端 + Windows 服务 `SamaDisplaySvc`，开机自动启动。
- **完全不依赖官方 `SAMA.exe`**：C# 直接驱动面板。
- 启动时发一次 `0xC8` 关键帧（921600 字节 BGRA，顺时针 90°）。
- 之后每 3 秒发一帧 **`0xCC` 私有增量帧**（C# 自研 `DeltaEncoder`，按官方算法逐字节还原）。
- 每帧前发 `0xCF` 查询；`needReSend:1` 时重载关键帧，不再每帧重连串口。
- CPU/GPU 温度、负载、频率、内存等实时刷新；时钟秒级跳动。
- 面板内置主题视频文件 `/mnt/SDCARD/video/4_90.mp4` 已删除，不会再恢复官方视频。

## 依赖：PawnIO

CPU 温度由 LibreHardwareMonitor 0.9.6 读取，需要 **PawnIO 2.2.0** 内核驱动。

- 安装包内置 `install\prereq\PawnIO_setup.exe`；若仓库里没有该二进制，`install-service.ps1` 会调用 `install\prereq\download-pawnio.ps1` 自动下载。
- 也可以手动安装：`winget install --id namazso.PawnIO --silent`。
- 未安装 PawnIO 时 CPU 温度会显示 `--`，GPU/负载/内存仍可用。

## 从源码构建

需要 .NET 8 SDK（或使用仓库外的便携 SDK）和 PowerShell 7。

```powershell
# 发布服务 + WPF 应用到 dist\
.\publish.ps1

# 以管理员身份安装服务、复制应用、注册开机自启
.\dist\install\install-service.ps1
```

`publish.ps1` 使用 `dotnet publish -r win-x64 --self-contained true`，目标机器无需安装 .NET。

## 目录结构

- `SamaDisplay.Core`：协议、C8 关键帧、0xCC 增量编码器、传感器、渲染、IPC
- `SamaDisplay.Service`：Windows 服务，开机启动并独占 COM9
- `SamaDisplay.App`：WPF 控制台 UI
- `install`：安装/卸载脚本、PawnIO 前置
- `docs/re`：0xCC 私有编码与协议还原报告
- `docs/dashboard-preview.png`：仪表盘预览

## 文件位置

- 应用：`C:\Program Files\SamaDisplay\App\SamaDisplay.exe`
- 服务：`C:\Program Files\SamaDisplay\Service\SamaDisplay.Service.exe`
- 配置：`C:\ProgramData\SamaDisplay\settings.json`
- 日志：`C:\ProgramData\SamaDisplay\service.log`

## 常用操作

- 打开界面：开始菜单 `SAMA Display`，或上面的 App 路径。
- 刷新间隔：`settings.json` 的 `IntervalSeconds`（默认 3 秒），改后重启服务。
- 手动重发关键帧：界面里“停止”→“启动”，或 `Restart-Service SamaDisplaySvc`。
- 卸载：`dist\install\uninstall-service.ps1`。

## 排障

- 屏幕只显示 SAMA logo：等待 10–20 秒让服务连上 COM9，或拔插一次 USB。
- CPU 温度 `--`：确认 PawnIO 驱动存在：`Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'"`。
- 若用官方 APP 重新设置了背景，视频文件会被重新上传；需要再删除一次 `/mnt/SDCARD/video/4_90.mp4`。

## Rust + Slint 控制面板原型

prototype/rust-slint 是用 Rust + Slint 重写的桌面控制面板原型，通过现有命名管道 SamaDisplay.IPC 读取状态并下发设置，不依赖 .NET/WPF 前端。

- 三页界面：仪表盘 / 显示与主题 / 服务与设置
- 实时状态：CPU/GPU 温度、负载、频率、内存、帧数、端口
- 可下发：start / stop / theme / brightness / interval / rotate
- 软件渲染，无需 OpenGL/C++ 运行时
- 截图与构建说明见 [prototype/rust-slint/README.md](prototype/rust-slint/README.md)
