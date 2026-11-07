$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$out = Join-Path $root 'dist'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'SamaDisplay.Service\SamaDisplay.Service.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $out 'service')
if ($LASTEXITCODE -ne 0) { throw 'service publish failed' }

dotnet publish (Join-Path $root 'SamaDisplay.App\SamaDisplay.App.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'app publish failed' }

Copy-Item (Join-Path $root 'install') (Join-Path $out 'install') -Recurse -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.md') -Force -ErrorAction SilentlyContinue
Write-Host "Published to $out"
