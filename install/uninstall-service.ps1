#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$service = Get-Service -Name 'SamaDisplaySvc' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SamaDisplaySvc' -Force }
    sc.exe delete SamaDisplaySvc | Out-Null
    Start-Sleep -Seconds 2
}

$targetRoot = Join-Path $env:ProgramFiles 'SamaDisplay'
if (Test-Path $targetRoot) { Remove-Item $targetRoot -Recurse -Force -ErrorAction SilentlyContinue }
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SAMA Display.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }

Write-Host "SAMA Display service removed. Settings in $env:ProgramData\SamaDisplay were kept."
