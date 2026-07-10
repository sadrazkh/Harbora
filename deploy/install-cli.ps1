# Harbora CLI installer (Windows). One command in PowerShell:
#   irm https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.ps1 | iex
#
# Downloads the self-contained harbora.exe from the latest GitHub release and adds it to your PATH.
# No .NET runtime required.
$ErrorActionPreference = 'Stop'

$repo = if ($env:HARBORA_REPO) { $env:HARBORA_REPO } else { 'sadrazkh/Harbora' }
$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$asset = "harbora-win-$arch.exe"
$url  = "https://github.com/$repo/releases/latest/download/$asset"

$dir = Join-Path $env:LOCALAPPDATA 'Harbora'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$exe = Join-Path $dir 'harbora.exe'

Write-Host "-> Downloading $asset ..."
try { Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing }
catch { Write-Error "Download failed. Is a release published for $asset?"; exit 1 }

# Add the install dir to the user PATH (persisted) if it isn't already there.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$dir*") {
  [Environment]::SetEnvironmentVariable('Path', "$userPath;$dir", 'User')
  $env:Path += ";$dir"
  Write-Host "Added $dir to your PATH (restart the terminal to pick it up everywhere)."
}

Write-Host "OK Installed to $exe"
Write-Host "Next:  harbora login --server https://panel.example.com --token <token>"
