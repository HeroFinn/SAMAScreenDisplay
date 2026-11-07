$ErrorActionPreference = 'Stop'
$root = if ($env:SAMA_ROOT) { $env:SAMA_ROOT } else { 'E:\AIWorkSpace' }
$pkg = Join-Path $root 'SamaDisplay-Rust'
$out = Join-Path $root 'SamaDisplay.Msi\Components.wxs'

function Esc([string]$value) { [System.Security.SecurityElement]::Escape($value) }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add('  <Fragment>')
$lines.Add('    <ComponentGroup Id="ProductComponents">')

# Rust front-end + shortcuts
$rust = Join-Path $pkg 'sama-display-slint.exe'
$lines.Add('      <Component Id="RustExeComponent" Directory="AppFolder" Guid="*">')
$lines.Add('        <File Id="RustExe" Name="SamaDisplay.Slint.exe" Source="' + (Esc $rust) + '" KeyPath="yes">')
$lines.Add('          <Shortcut Id="StartMenuShortcut" Directory="AppShortcutFolder" Name="SAMA Display (Rust)" WorkingDirectory="AppFolder" Advertise="no" />')
$lines.Add('          <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="SAMA Display (Rust)" WorkingDirectory="AppFolder" Advertise="no" />')
$lines.Add('        </File>')
$lines.Add('      </Component>')

# Service executable owns the Windows service definition.
$serviceDir = Join-Path $pkg 'service'
$serviceExe = Join-Path $serviceDir 'SamaDisplay.Service.exe'
$lines.Add('      <Component Id="ServiceExeComponent" Directory="ServiceFolder" Guid="*">')
$lines.Add('        <File Id="ServiceExe" Source="' + (Esc $serviceExe) + '" KeyPath="yes" />')
$lines.Add('        <ServiceInstall Id="SamaDisplayServiceInstall" Name="SamaDisplaySvc" DisplayName="SAMA Display Service" Description="Renders hardware telemetry to the SAMA 3.4&quot; USB LCD." Type="ownProcess" Start="auto" ErrorControl="normal" Account="LocalSystem" />')
$lines.Add('        <ServiceControl Id="SamaDisplayServiceControl" Name="SamaDisplaySvc" Start="install" Stop="both" Remove="uninstall" Wait="yes" />')
$lines.Add('      </Component>')

# Remaining service files.
$i = 0
foreach ($file in (Get-ChildItem $serviceDir -Recurse -File | Sort-Object FullName)) {
    if ($file.FullName -eq $serviceExe) { continue }
    $lines.Add('      <Component Id="ServiceFile' + $i + '" Directory="ServiceFolder" Guid="*">')
    $lines.Add('        <File Id="ServiceFile' + $i + 'File" Source="' + (Esc $file.FullName) + '" KeyPath="yes" />')
    $lines.Add('      </Component>')
    $i++
}

# Installer scripts + PawnIO prereq.
$installDir = Join-Path $pkg 'install'
$j = 0
foreach ($file in (Get-ChildItem $installDir -Recurse -File | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($installDir.Length).TrimStart('\')
    $dirId = if ($relative -like 'prereq\*') { 'PrereqFolder' } else { 'InstallFolder' }
    $fileId = if ($file.Name -eq 'PawnIO_setup.exe') { 'PawnIOSetup' } else { "InstallFile$j`File" }
    $lines.Add('      <Component Id="InstallFile' + $j + '" Directory="' + $dirId + '" Guid="*">')
    $lines.Add('        <File Id="' + $fileId + '" Source="' + (Esc $file.FullName) + '" KeyPath="yes" />')
    $lines.Add('      </Component>')
    $j++
}

$lines.Add('    </ComponentGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')
Set-Content -Path $out -Value $lines -Encoding UTF8
Write-Host "Generated $out ($($lines.Count) lines, service=$($i + 1), install=$j)"
