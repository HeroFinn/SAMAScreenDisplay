$ErrorActionPreference = 'Stop'
$env:CARGO_HOME = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $PSScriptRoot '..\.cargo' }
$env:RUSTUP_HOME = if ($env:RUSTUP_HOME) { $env:RUSTUP_HOME } else { Join-Path $PSScriptRoot '..\.rustup' }
$env:PATH = "$env:CARGO_HOME\bin;" + $env:PATH

$mingw = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'BrechtSanders.WinLibs.POSIX.MSVCRT_*' } |
    ForEach-Object { Join-Path $_.FullName 'mingw64\bin' } |
    Where-Object { Test-Path (Join-Path $_ 'x86_64-w64-mingw32-gcc.exe') } |
    Select-Object -First 1

if (-not $mingw) {
    $mingw = Get-ChildItem 'C:\Program Files' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'WinLibs|mingw' } |
        ForEach-Object { Join-Path $_.FullName 'bin' } |
        Where-Object { Test-Path (Join-Path $_ 'x86_64-w64-mingw32-gcc.exe') } |
        Select-Object -First 1
}
if (-not $mingw) { throw 'WinLibs MinGW not found. Install BrechtSanders.WinLibs.POSIX.MSVCRT.' }

$toolchain = Get-ChildItem "$env:RUSTUP_HOME\toolchains" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'stable-x86_64-pc-windows-gnu' } |
    Select-Object -First 1

$env:PATH = "$mingw;" + $(if ($toolchain) { "$($toolchain.FullName)\bin;" } else { '' }) + $env:PATH
$env:CC = 'x86_64-w64-mingw32-gcc'

Set-Location $PSScriptRoot
cargo build --release
Write-Host "Built: $PSScriptRoot\target\release\sama-display-slint.exe"
