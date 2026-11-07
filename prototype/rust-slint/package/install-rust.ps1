#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host 'Installing SAMA Display service ...'
& (Join-Path $root 'install\install-service.ps1')

$appDir = Join-Path $env:ProgramFiles 'SamaDisplay\App'
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
$exe = Join-Path $appDir 'SamaDisplay.Slint.exe'
Copy-Item (Join-Path $root 'sama-display-slint.exe') $exe -Force

# Keep installer assets next to the installed app so the UI's
# install/repair/uninstall buttons can find them.
$dataRoot = Join-Path $env:ProgramFiles 'SamaDisplay'
Copy-Item (Join-Path $root 'install') (Join-Path $dataRoot 'install') -Recurse -Force
Copy-Item (Join-Path $root 'service') (Join-Path $dataRoot 'service') -Recurse -Force

$ws = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SAMA Display (Rust).lnk'
$lnk = $ws.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $appDir
$lnk.Description = 'SAMA Display · Rust + Slint'
$lnk.Save()

$desktop = [Environment]::GetFolderPath('Desktop')
if ($desktop) {
    $desktopLnk = $ws.CreateShortcut((Join-Path $desktop 'SAMA Display (Rust).lnk'))
    $desktopLnk.TargetPath = $exe
    $desktopLnk.WorkingDirectory = $appDir
    $desktopLnk.Description = 'SAMA Display · Rust + Slint'
    $desktopLnk.Save()
}

Write-Host "Installed: $exe"
Write-Host 'Launch it from the Start Menu or Desktop shortcut.'
