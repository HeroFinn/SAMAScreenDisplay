# MSI 构建

MSI 使用 WiX Toolset 5，包含：

- Rust/Slint 前端
- C# 自包含服务（`ServiceInstall` / `ServiceControl`）
- 安装脚本（供 UI 的安装/卸载按钮使用）
- PawnIO 安装器与自定义动作
- 开始菜单/桌面快捷方式
- 服务 ACL（SDDL 自定义动作）

## 构建

1. 先准备发布包目录（`SamaDisplay-Rust`：`sama-display-slint.exe`、`service/`、`install/`）。
2. 生成组件清单：

```powershell
$env:SAMA_ROOT = 'E:\AIWorkSpace'   # 包含 SamaDisplay-Rust 的目录
.\generate.ps1
```

3. 构建 x64 MSI：

```powershell
wix build Package.wxs Components.wxs -arch x64 -o SamaDisplay.msi
```

需要 WiX 5：

```powershell
dotnet tool install --tool-path .\tools\wix wix --version 5.0.2
```

## 安装/卸载

```powershell
msiexec /i SamaDisplay.msi /qn
msiexec /x SamaDisplay.msi /qn
```
