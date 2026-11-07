# Downloads the signed PawnIO installer used for CPU temperature access.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'PawnIO_setup.exe'
$url = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
Write-Host "Downloading PawnIO 2.2.0 ..."
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
$hash = (Get-FileHash $dest -Algorithm SHA256).Hash
if ($hash -ne '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032') {
    throw "PawnIO SHA256 mismatch: $hash"
}
Write-Host "PawnIO installer ready: $dest"
