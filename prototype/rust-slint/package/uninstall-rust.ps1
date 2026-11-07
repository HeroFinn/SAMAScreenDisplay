#Requires -RunAsAdministrator
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

Get-Process sama-display-slint -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$appDir = Join-Path $env:ProgramFiles 'SamaDisplay\App'
Remove-Item (Join-Path $appDir 'SamaDisplay.Slint.exe') -Force -ErrorAction SilentlyContinue

$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SAMA Display (Rust).lnk'
Remove-Item $startMenu -Force -ErrorAction SilentlyContinue
$desktop = [Environment]::GetFolderPath('Desktop')
if ($desktop) { Remove-Item (Join-Path $desktop 'SAMA Display (Rust).lnk') -Force -ErrorAction SilentlyContinue }

& (Join-Path $root 'install\uninstall-service.ps1')
Write-Host 'SAMA Display (Rust) uninstalled.'
