#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

# CPU temperature needs the PawnIO kernel driver (bundled installer).
$pawn = Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'" -ErrorAction SilentlyContinue
if (-not $pawn) {
    $pawnSetup = Join-Path $PSScriptRoot 'prereq\PawnIO_setup.exe'
    if (Test-Path $pawnSetup) {
        Write-Host 'Installing PawnIO driver (CPU temperature)...'
        try { Start-Process -FilePath $pawnSetup -ArgumentList '-install','-silent' -Wait } catch {
            Write-Warning ('PawnIO install failed: ' + $_.Exception.Message)
        }
    }
    $pawn = Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'" -ErrorAction SilentlyContinue
    if (-not $pawn) {
        Write-Warning 'PawnIO is missing; CPU temperature will show -- until it is installed.'
    }
}

$root = Split-Path $PSScriptRoot -Parent
$serviceSource = Join-Path $root 'dist\service'
if (-not (Test-Path (Join-Path $serviceSource 'SamaDisplay.Service.exe'))) {
    $serviceSource = Join-Path $root 'service'
}
if (-not (Test-Path (Join-Path $serviceSource 'SamaDisplay.Service.exe'))) {
    throw "Published service not found. Run publish.ps1 first."
}

$service = Get-Service -Name 'SamaDisplaySvc' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SamaDisplaySvc' -Force }
    sc.exe delete SamaDisplaySvc | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name 'SamaDisplay.Service' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 1
}

$targetRoot = Join-Path $env:ProgramFiles 'SamaDisplay'
$serviceTarget = Join-Path $targetRoot 'Service'
New-Item -ItemType Directory -Force -Path $serviceTarget | Out-Null
Copy-Item (Join-Path $serviceSource '*') $serviceTarget -Recurse -Force
$exe = Join-Path $serviceTarget 'SamaDisplay.Service.exe'

New-Service -Name 'SamaDisplaySvc' -BinaryPathName "`"$exe`"" -DisplayName 'SAMA Display Service' `
    -Description 'Renders hardware telemetry to the SAMA 3.4" USB LCD at boot and serves the SAMA Display UI over a local named pipe.' `
    -StartupType Automatic | Out-Null

sc.exe failure SamaDisplaySvc reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Allow normal users to start/stop/query the service so the desktop app can
# bring the backend up without an elevation prompt on every launch.
$sddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;AU)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)'
sc.exe sdset SamaDisplaySvc $sddl | Out-Null

Start-Service -Name 'SamaDisplaySvc'

$dataDir = Join-Path $env:ProgramData 'SamaDisplay'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$settings = Join-Path $dataDir 'settings.json'
if (-not (Test-Path $settings)) {
    @'
{
  "Port": null,
  "Brightness": 170,
  "IntervalSeconds": 3,
  "Theme": "Dark",
  "ShowClock": true,
  "Fahrenheit": false,
  "Rotate": 0,
  "JpegQuality": 30,
  "AutoStartDisplay": true,
  "StartMinimized": true
}
'@ | Set-Content -Path $settings -Encoding UTF8
}

$appSource = Join-Path $root 'dist\app'
if (-not (Test-Path (Join-Path $appSource 'SamaDisplay.exe'))) {
    $appSource = Join-Path $root 'app'
}
if (Test-Path (Join-Path $appSource 'SamaDisplay.exe')) {
    $appTarget = Join-Path $targetRoot 'App'
    New-Item -ItemType Directory -Force -Path $appTarget | Out-Null
    Copy-Item (Join-Path $appSource '*') $appTarget -Recurse -Force
    $shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SAMA Display.lnk'
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($shortcut)
    $lnk.TargetPath = Join-Path $appTarget 'SamaDisplay.exe'
    $lnk.WorkingDirectory = $appTarget
    $lnk.Description = 'SAMA Display'
    $lnk.Save()
}

Get-Service SamaDisplaySvc | Select-Object Name, Status, StartType
Write-Host "SAMA Display service installed. Log: $dataDir\service.log"
